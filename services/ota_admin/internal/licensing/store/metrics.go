package store

import (
	"context"
	"database/sql/driver"
	"errors"
	"io"
	"reflect"
	"strings"
	"sync/atomic"

	"modernc.org/sqlite"
)

type Metrics struct {
	Busy   uint64 `json:"busy"`
	Locked uint64 `json:"locked"`
}

type metricCounters struct {
	busy   atomic.Uint64
	locked atomic.Uint64
}

func (m *metricCounters) snapshot() Metrics {
	if m == nil {
		return Metrics{}
	}
	return Metrics{Busy: m.busy.Load(), Locked: m.locked.Load()}
}

func classifySQLiteError(counters *metricCounters, err error) error {
	if err == nil || errors.Is(err, io.EOF) || errors.Is(err, context.Canceled) || errors.Is(err, context.DeadlineExceeded) {
		return err
	}
	var coded interface{ Code() int }
	if errors.As(err, &coded) {
		switch coded.Code() & 0xff {
		case 5:
			counters.busy.Add(1)
			return err
		case 6:
			counters.locked.Add(1)
			return err
		}
	}
	message := strings.ToLower(err.Error())
	switch {
	case strings.Contains(message, "sqlite_locked") || strings.Contains(message, "database table is locked"):
		counters.locked.Add(1)
	case strings.Contains(message, "sqlite_busy") || strings.Contains(message, "database is locked"):
		counters.busy.Add(1)
	}
	return err
}

type metricsConnector struct {
	driver   driver.Driver
	name     string
	counters *metricCounters
}

func newMetricsConnector(name string, counters *metricCounters) driver.Connector {
	return &metricsConnector{driver: &sqlite.Driver{}, name: name, counters: counters}
}

func (c *metricsConnector) Connect(context.Context) (driver.Conn, error) {
	conn, err := c.driver.Open(c.name)
	if err != nil {
		return nil, classifySQLiteError(c.counters, err)
	}
	return &metricsConn{Conn: conn, counters: c.counters}, nil
}

func (c *metricsConnector) Driver() driver.Driver { return c.driver }

type metricsConn struct {
	driver.Conn
	counters *metricCounters
}

func (c *metricsConn) Close() error {
	return classifySQLiteError(c.counters, c.Conn.Close())
}

func (c *metricsConn) Prepare(query string) (driver.Stmt, error) {
	stmt, err := c.Conn.Prepare(query)
	if err != nil {
		return nil, classifySQLiteError(c.counters, err)
	}
	return &metricsStmt{Stmt: stmt, counters: c.counters}, nil
}

func (c *metricsConn) Begin() (driver.Tx, error) {
	tx, err := c.Conn.Begin()
	if err != nil {
		return nil, classifySQLiteError(c.counters, err)
	}
	return &metricsTx{Tx: tx, counters: c.counters}, nil
}

func (c *metricsConn) PrepareContext(ctx context.Context, query string) (driver.Stmt, error) {
	if conn, ok := c.Conn.(driver.ConnPrepareContext); ok {
		stmt, err := conn.PrepareContext(ctx, query)
		if err != nil {
			return nil, classifySQLiteError(c.counters, err)
		}
		return &metricsStmt{Stmt: stmt, counters: c.counters}, nil
	}
	return c.Prepare(query)
}

func (c *metricsConn) BeginTx(ctx context.Context, opts driver.TxOptions) (driver.Tx, error) {
	if conn, ok := c.Conn.(driver.ConnBeginTx); ok {
		tx, err := conn.BeginTx(ctx, opts)
		if err != nil {
			return nil, classifySQLiteError(c.counters, err)
		}
		return &metricsTx{Tx: tx, counters: c.counters}, nil
	}
	return c.Begin()
}

func (c *metricsConn) ExecContext(ctx context.Context, query string, args []driver.NamedValue) (driver.Result, error) {
	if conn, ok := c.Conn.(driver.ExecerContext); ok {
		result, err := conn.ExecContext(ctx, query, args)
		return metricsResult{Result: result, counters: c.counters}, classifySQLiteError(c.counters, err)
	}
	return nil, driver.ErrSkip
}

func (c *metricsConn) QueryContext(ctx context.Context, query string, args []driver.NamedValue) (driver.Rows, error) {
	if conn, ok := c.Conn.(driver.QueryerContext); ok {
		rows, err := conn.QueryContext(ctx, query, args)
		if err != nil {
			return nil, classifySQLiteError(c.counters, err)
		}
		return &metricsRows{Rows: rows, counters: c.counters}, nil
	}
	return nil, driver.ErrSkip
}

func (c *metricsConn) Ping(ctx context.Context) error {
	if conn, ok := c.Conn.(driver.Pinger); ok {
		return classifySQLiteError(c.counters, conn.Ping(ctx))
	}
	return nil
}

func (c *metricsConn) CheckNamedValue(value *driver.NamedValue) error {
	if conn, ok := c.Conn.(driver.NamedValueChecker); ok {
		return classifySQLiteError(c.counters, conn.CheckNamedValue(value))
	}
	return driver.ErrSkip
}

func (c *metricsConn) ResetSession(ctx context.Context) error {
	if conn, ok := c.Conn.(driver.SessionResetter); ok {
		return classifySQLiteError(c.counters, conn.ResetSession(ctx))
	}
	return nil
}

func (c *metricsConn) IsValid() bool {
	if conn, ok := c.Conn.(driver.Validator); ok {
		return conn.IsValid()
	}
	return true
}

type metricsStmt struct {
	driver.Stmt
	counters *metricCounters
}

func (s *metricsStmt) Close() error {
	return classifySQLiteError(s.counters, s.Stmt.Close())
}

func (s *metricsStmt) Exec(args []driver.Value) (driver.Result, error) {
	result, err := s.Stmt.Exec(args)
	return metricsResult{Result: result, counters: s.counters}, classifySQLiteError(s.counters, err)
}

func (s *metricsStmt) Query(args []driver.Value) (driver.Rows, error) {
	rows, err := s.Stmt.Query(args)
	if err != nil {
		return nil, classifySQLiteError(s.counters, err)
	}
	return &metricsRows{Rows: rows, counters: s.counters}, nil
}

func (s *metricsStmt) ExecContext(ctx context.Context, args []driver.NamedValue) (driver.Result, error) {
	if stmt, ok := s.Stmt.(driver.StmtExecContext); ok {
		result, err := stmt.ExecContext(ctx, args)
		return metricsResult{Result: result, counters: s.counters}, classifySQLiteError(s.counters, err)
	}
	return nil, driver.ErrSkip
}

func (s *metricsStmt) QueryContext(ctx context.Context, args []driver.NamedValue) (driver.Rows, error) {
	if stmt, ok := s.Stmt.(driver.StmtQueryContext); ok {
		rows, err := stmt.QueryContext(ctx, args)
		if err != nil {
			return nil, classifySQLiteError(s.counters, err)
		}
		return &metricsRows{Rows: rows, counters: s.counters}, nil
	}
	return nil, driver.ErrSkip
}

type metricsTx struct {
	driver.Tx
	counters *metricCounters
}

func (t *metricsTx) Commit() error   { return classifySQLiteError(t.counters, t.Tx.Commit()) }
func (t *metricsTx) Rollback() error { return classifySQLiteError(t.counters, t.Tx.Rollback()) }

type metricsRows struct {
	driver.Rows
	counters *metricCounters
}

func (r *metricsRows) Close() error {
	return classifySQLiteError(r.counters, r.Rows.Close())
}

func (r *metricsRows) Next(values []driver.Value) error {
	return classifySQLiteError(r.counters, r.Rows.Next(values))
}

func (r *metricsRows) ColumnTypeDatabaseTypeName(index int) string {
	if rows, ok := r.Rows.(driver.RowsColumnTypeDatabaseTypeName); ok {
		return rows.ColumnTypeDatabaseTypeName(index)
	}
	return ""
}

func (r *metricsRows) ColumnTypeLength(index int) (int64, bool) {
	if rows, ok := r.Rows.(driver.RowsColumnTypeLength); ok {
		return rows.ColumnTypeLength(index)
	}
	return 0, false
}

func (r *metricsRows) ColumnTypeNullable(index int) (bool, bool) {
	if rows, ok := r.Rows.(driver.RowsColumnTypeNullable); ok {
		return rows.ColumnTypeNullable(index)
	}
	return false, false
}

func (r *metricsRows) ColumnTypePrecisionScale(index int) (int64, int64, bool) {
	if rows, ok := r.Rows.(driver.RowsColumnTypePrecisionScale); ok {
		return rows.ColumnTypePrecisionScale(index)
	}
	return 0, 0, false
}

func (r *metricsRows) ColumnTypeScanType(index int) reflect.Type {
	if rows, ok := r.Rows.(driver.RowsColumnTypeScanType); ok {
		return rows.ColumnTypeScanType(index)
	}
	return reflect.TypeFor[any]()
}

func (r *metricsRows) HasNextResultSet() bool {
	if rows, ok := r.Rows.(driver.RowsNextResultSet); ok {
		return rows.HasNextResultSet()
	}
	return false
}

func (r *metricsRows) NextResultSet() error {
	if rows, ok := r.Rows.(driver.RowsNextResultSet); ok {
		return classifySQLiteError(r.counters, rows.NextResultSet())
	}
	return io.EOF
}

type metricsResult struct {
	driver.Result
	counters *metricCounters
}

func (r metricsResult) LastInsertId() (int64, error) {
	if r.Result == nil {
		return 0, errors.New("SQLite operation returned no result")
	}
	value, err := r.Result.LastInsertId()
	return value, classifySQLiteError(r.counters, err)
}

func (r metricsResult) RowsAffected() (int64, error) {
	if r.Result == nil {
		return 0, errors.New("SQLite operation returned no result")
	}
	value, err := r.Result.RowsAffected()
	return value, classifySQLiteError(r.counters, err)
}
