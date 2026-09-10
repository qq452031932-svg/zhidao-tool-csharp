package store

import (
	"context"
	"database/sql"
	"database/sql/driver"
	"errors"
	"io"
	"path/filepath"
	"reflect"
	"testing"
)

func TestMetricsCountRealSQLiteContentionButNotDomainErrors(t *testing.T) {
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "metrics.db")
	store, err := Open(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer store.Close()

	store.db.SetMaxOpenConns(1)
	if _, err := store.db.ExecContext(ctx, `PRAGMA busy_timeout=1`); err != nil {
		t.Fatal(err)
	}
	locker, err := sql.Open("sqlite", path)
	if err != nil {
		t.Fatal(err)
	}
	defer locker.Close()
	conn, err := locker.Conn(ctx)
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close()
	if _, err := conn.ExecContext(ctx, `PRAGMA busy_timeout=1`); err != nil {
		t.Fatal(err)
	}
	if _, err := conn.ExecContext(ctx, `BEGIN IMMEDIATE`); err != nil {
		t.Fatal(err)
	}
	if _, err := store.CreateSoftware(ctx, "Contended Product"); err == nil {
		t.Fatal("write unexpectedly succeeded while SQLite was locked")
	}
	if _, err := conn.ExecContext(ctx, `ROLLBACK`); err != nil {
		t.Fatal(err)
	}
	contention := store.Metrics()
	if contention.Busy+contention.Locked == 0 {
		t.Fatalf("contention metrics = %+v, want at least one busy/locked event", contention)
	}

	created, err := store.CreateSoftware(ctx, "Unique Product")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := store.CreateSoftware(ctx, created.Name); !errors.Is(err, ErrConflict) {
		t.Fatalf("duplicate error = %v, want ErrConflict", err)
	}
	if err := store.RenameSoftware(ctx, "missing", "Still Missing"); !errors.Is(err, ErrNotFound) {
		t.Fatalf("missing error = %v, want ErrNotFound", err)
	}
	afterDomainErrors := store.Metrics()
	if afterDomainErrors != contention {
		t.Fatalf("domain errors changed metrics: before=%+v after=%+v", contention, afterDomainErrors)
	}
}

func TestSQLiteClassifierDistinguishesBusyLockedAndIgnoresOtherErrors(t *testing.T) {
	var counters metricCounters
	busy := errors.New("database is locked (5) (SQLITE_BUSY)")
	locked := errors.New("database table is locked (6) (SQLITE_LOCKED)")
	other := errors.New("constraint failed")
	if classifySQLiteError(&counters, busy) != busy || classifySQLiteError(&counters, locked) != locked || classifySQLiteError(&counters, other) != other {
		t.Fatal("classifier changed returned error identity")
	}
	if got := counters.snapshot(); got != (Metrics{Busy: 1, Locked: 1}) {
		t.Fatalf("metrics = %+v", got)
	}
}

type codedCloseError struct{ code int }

func (e *codedCloseError) Error() string { return "controlled SQLite close error" }
func (e *codedCloseError) Code() int     { return e.code }

type closeErrorConn struct{ err error }

func (c *closeErrorConn) Prepare(string) (driver.Stmt, error) { return nil, driver.ErrSkip }
func (c *closeErrorConn) Close() error                        { return c.err }
func (c *closeErrorConn) Begin() (driver.Tx, error)           { return nil, driver.ErrSkip }

type closeErrorStmt struct{ err error }

func (s *closeErrorStmt) Close() error                               { return s.err }
func (s *closeErrorStmt) NumInput() int                              { return -1 }
func (s *closeErrorStmt) Exec([]driver.Value) (driver.Result, error) { return nil, driver.ErrSkip }
func (s *closeErrorStmt) Query([]driver.Value) (driver.Rows, error)  { return nil, driver.ErrSkip }

type metadataRows struct {
	closeErr      error
	nextResultErr error
}

func (r *metadataRows) Columns() []string                     { return []string{"value"} }
func (r *metadataRows) Close() error                          { return r.closeErr }
func (r *metadataRows) Next([]driver.Value) error             { return io.EOF }
func (r *metadataRows) ColumnTypeDatabaseTypeName(int) string { return "TEXT" }
func (r *metadataRows) ColumnTypeLength(int) (int64, bool)    { return 128, true }
func (r *metadataRows) ColumnTypeNullable(int) (bool, bool)   { return true, true }
func (r *metadataRows) ColumnTypePrecisionScale(int) (int64, int64, bool) {
	return 12, 3, true
}
func (r *metadataRows) ColumnTypeScanType(int) reflect.Type { return reflect.TypeOf("") }
func (r *metadataRows) HasNextResultSet() bool              { return true }
func (r *metadataRows) NextResultSet() error                { return r.nextResultErr }

func TestMetricsWrappersClassifyCloseErrorsExactlyOnce(t *testing.T) {
	tests := []struct {
		name string
		code int
		wrap func(*metricCounters, error) interface{ Close() error }
		want Metrics
	}{
		{name: "connection busy", code: 5, wrap: func(c *metricCounters, err error) interface{ Close() error } {
			return &metricsConn{Conn: &closeErrorConn{err: err}, counters: c}
		}, want: Metrics{Busy: 1}},
		{name: "statement locked", code: 6, wrap: func(c *metricCounters, err error) interface{ Close() error } {
			return &metricsStmt{Stmt: &closeErrorStmt{err: err}, counters: c}
		}, want: Metrics{Locked: 1}},
		{name: "rows busy", code: 5, wrap: func(c *metricCounters, err error) interface{ Close() error } {
			return &metricsRows{Rows: &metadataRows{closeErr: err}, counters: c}
		}, want: Metrics{Busy: 1}},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			var counters metricCounters
			wantErr := &codedCloseError{code: tt.code}
			gotErr := tt.wrap(&counters, wantErr).Close()
			if gotErr != wantErr {
				t.Fatalf("Close error identity = %v, want %v", gotErr, wantErr)
			}
			if got := counters.snapshot(); got != tt.want {
				t.Fatalf("metrics = %+v, want %+v", got, tt.want)
			}
		})
	}
}

func TestMetricsRowsForwardsOptionalMetadataAndNextResultSet(t *testing.T) {
	var counters metricCounters
	wantErr := &codedCloseError{code: 6}
	rows := &metricsRows{Rows: &metadataRows{nextResultErr: wantErr}, counters: &counters}
	databaseType, ok := any(rows).(driver.RowsColumnTypeDatabaseTypeName)
	if !ok || databaseType.ColumnTypeDatabaseTypeName(0) != "TEXT" {
		t.Fatal("database type metadata was not forwarded")
	}
	length, ok := any(rows).(driver.RowsColumnTypeLength)
	if !ok {
		t.Fatal("length metadata capability was not forwarded")
	}
	if got, valid := length.ColumnTypeLength(0); !valid || got != 128 {
		t.Fatalf("length = %d/%v", got, valid)
	}
	nullable, ok := any(rows).(driver.RowsColumnTypeNullable)
	if !ok {
		t.Fatal("nullable metadata capability was not forwarded")
	}
	if got, valid := nullable.ColumnTypeNullable(0); !valid || !got {
		t.Fatalf("nullable = %v/%v", got, valid)
	}
	precision, ok := any(rows).(driver.RowsColumnTypePrecisionScale)
	if !ok {
		t.Fatal("precision metadata capability was not forwarded")
	}
	if gotPrecision, gotScale, valid := precision.ColumnTypePrecisionScale(0); !valid || gotPrecision != 12 || gotScale != 3 {
		t.Fatalf("precision/scale = %d/%d/%v", gotPrecision, gotScale, valid)
	}
	scanType, ok := any(rows).(driver.RowsColumnTypeScanType)
	if !ok || scanType.ColumnTypeScanType(0) != reflect.TypeOf("") {
		t.Fatal("scan type metadata was not forwarded")
	}
	nextResult, ok := any(rows).(driver.RowsNextResultSet)
	if !ok || !nextResult.HasNextResultSet() {
		t.Fatal("next-result-set capability was not forwarded")
	}
	if gotErr := nextResult.NextResultSet(); gotErr != wantErr {
		t.Fatalf("NextResultSet error identity = %v, want %v", gotErr, wantErr)
	}
	if got := counters.snapshot(); got != (Metrics{Locked: 1}) {
		t.Fatalf("NextResultSet metrics = %+v", got)
	}
}
