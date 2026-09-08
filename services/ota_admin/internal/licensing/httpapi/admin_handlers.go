package httpapi

import (
	"bytes"
	"context"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/csv"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"strconv"
	"strings"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/licensing/model"
	"fanfh.cn/superpan/ota-admin/internal/licensing/service"
	"github.com/google/uuid"
)

var adminNameFields = map[string]struct{}{"name": {}}
var adminEmptyFields = map[string]struct{}{}
var adminPackageFields = map[string]struct{}{"name": {}, "software_ids": {}}
var adminMembersFields = map[string]struct{}{"software_ids": {}}
var adminGenerateFields = map[string]struct{}{"package_id": {}, "duration_days": {}, "count": {}, "default_note": {}, "per_card_notes": {}}
var adminExportFields = map[string]struct{}{"card_ids": {}}
var adminRemarkFields = map[string]struct{}{"note": {}}
var adminRenewFields = map[string]struct{}{"duration_days": {}}

type adminNameRequest struct {
	Name string `json:"name"`
}

type adminPackageRequest struct {
	Name        string   `json:"name"`
	SoftwareIDs []string `json:"software_ids"`
}

type adminMembersRequest struct {
	SoftwareIDs []string `json:"software_ids"`
}

type adminGenerateRequest struct {
	PackageID    string    `json:"package_id"`
	DurationDays int       `json:"duration_days"`
	Count        int       `json:"count"`
	DefaultNote  *string   `json:"default_note"`
	PerCardNotes []*string `json:"per_card_notes"`
}
type adminExportRequest struct {
	CardIDs []string `json:"card_ids"`
}
type adminRemarkRequest struct {
	Note *string `json:"note"`
}
type adminRenewRequest struct {
	DurationDays int `json:"duration_days"`
}

func (a *api) authorizeInternal(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		peer := canonicalRemoteIP(r.RemoteAddr)
		if !peer.IsValid() || !peer.IsLoopback() {
			a.writeAdminError(w, http.StatusNotFound, "not_found")
			return
		}
		values := r.Header.Values("Authorization")
		candidate := ""
		if len(values) > 0 {
			candidate = values[0]
		}
		expected := make([]byte, 0, len("Bearer ")+len(a.options.InternalSecret))
		expected = append(expected, "Bearer "...)
		expected = append(expected, a.options.InternalSecret...)
		candidateHash := sha256.Sum256([]byte(candidate))
		expectedHash := sha256.Sum256(expected)
		credentialMatch := subtle.ConstantTimeCompare(candidateHash[:], expectedHash[:]) == 1
		valid := len(values) == 1 && len(candidate) == len(expected) && !strings.Contains(candidate, ",") && credentialMatch
		clear(expected)
		if !valid {
			a.writeAdminError(w, http.StatusUnauthorized, "unauthorized")
			return
		}
		if malformedInternalPath(r.URL.Path) {
			a.writeAdminError(w, http.StatusNotFound, "not_found")
			return
		}
		if !a.allowAdmin(w, r) {
			return
		}
		if headerBytes(r.Header) > a.options.MaxHeaderBytes {
			a.writeAdminError(w, http.StatusBadRequest, "invalid_request")
			return
		}
		if mux, ok := next.(*http.ServeMux); ok {
			handler, pattern := mux.Handler(r)
			if pattern == "" {
				probe := &bufferedAdminResponse{header: make(http.Header)}
				handler.ServeHTTP(probe, r)
				status := http.StatusNotFound
				if probe.status == http.StatusMethodNotAllowed {
					status = http.StatusMethodNotAllowed
					if allow := probe.header.Get("Allow"); allow != "" {
						w.Header().Set("Allow", allow)
					}
				}
				a.writeAdminError(w, status, map[bool]string{true: "method_not_allowed", false: "not_found"}[status == http.StatusMethodNotAllowed])
				return
			}
		}
		if r.Method != http.MethodGet && r.Method != http.MethodHead {
			a.serveAdminMutation(w, r, next)
			return
		}
		next.ServeHTTP(w, r)
	})
}

func malformedInternalPath(path string) bool {
	if path == "" || strings.Contains(path, "//") || strings.Contains(path, `\`) {
		return true
	}
	for _, segment := range strings.Split(path, "/") {
		if segment == "." || segment == ".." {
			return true
		}
	}
	return false
}

type bufferedAdminResponse struct {
	header http.Header
	status int
	body   bytes.Buffer
}

func (w *bufferedAdminResponse) Header() http.Header { return w.header }
func (w *bufferedAdminResponse) WriteHeader(status int) {
	if w.status == 0 {
		w.status = status
	}
}
func (w *bufferedAdminResponse) Write(data []byte) (int, error) {
	if w.status == 0 {
		w.status = http.StatusOK
	}
	return w.body.Write(data)
}

func (a *api) serveAdminMutation(w http.ResponseWriter, r *http.Request, next http.Handler) {
	key, ok := a.idempotencyKey(w, r)
	if !ok {
		return
	}
	if r.ContentLength > a.options.MaxBodyBytes {
		a.writeAdminError(w, http.StatusBadRequest, "invalid_request")
		return
	}
	body, err := io.ReadAll(http.MaxBytesReader(w, r.Body, a.options.MaxBodyBytes))
	if err != nil {
		a.writeAdminError(w, http.StatusBadRequest, "invalid_request")
		return
	}
	r.Body = io.NopCloser(bytes.NewReader(body))
	prefix := []byte(r.Method + "\x00" + r.URL.RequestURI() + "\x00")
	sum := sha256.Sum256(append(prefix, body...))
	fingerprint := hex.EncodeToString(sum[:])

	now := a.options.Clock.Now().UTC()
	expiresAt := now.Add(24 * time.Hour)
	tx, record, outcome, err := a.options.AdminIdempotencyStore.BeginAdminMutation(r.Context(), key, fingerprint, now, expiresAt, a.options.MaxAdminIdempotencyEntries, a.options.MaxAdminIdempotencyBytes)
	if err != nil {
		a.writeAdminError(w, http.StatusServiceUnavailable, "service_unavailable")
		return
	}
	switch outcome {
	case model.AdminIdempotencyConflict:
		a.writeAdminError(w, http.StatusConflict, "idempotency_conflict")
		return
	case model.AdminIdempotencyPending:
		a.writeAdminError(w, http.StatusConflict, "operation_in_progress")
		return
	case model.AdminIdempotencyReplay:
		replayBody, err := a.options.AdminService.OpenAdminReplay(key, record.ReplayKeyID, record.Body)
		if err != nil {
			a.writeAdminError(w, http.StatusServiceUnavailable, "service_unavailable")
			return
		}
		defer clear(replayBody)
		w.Header().Set("Content-Type", record.ContentType)
		w.Header().Set("Cache-Control", "no-store")
		if record.ContentDisposition != "" {
			w.Header().Set("Content-Disposition", record.ContentDisposition)
		}
		w.WriteHeader(record.Status)
		_, _ = w.Write(replayBody)
		return
	case model.AdminIdempotencyNew:
		// Continue below with the SQLite write transaction held.
	default:
		a.writeAdminError(w, http.StatusServiceUnavailable, "service_unavailable")
		return
	}
	defer tx.Rollback()
	r = r.WithContext(service.WithAdminMutationTransaction(r.Context(), tx))
	buffered := &bufferedAdminResponse{header: make(http.Header)}
	next.ServeHTTP(buffered, r)
	if buffered.status == 0 {
		buffered.status = http.StatusOK
	}
	replayKeyID, protected, err := a.options.AdminService.ProtectAdminReplay(key, buffered.body.Bytes())
	if err != nil {
		a.writeAdminError(w, http.StatusServiceUnavailable, "service_unavailable")
		return
	}
	record = model.AdminIdempotencyRecord{Fingerprint: fingerprint, Status: buffered.status, Body: protected, ReplayKeyID: replayKeyID, ContentType: buffered.header.Get("Content-Type"), ContentDisposition: buffered.header.Get("Content-Disposition"), ExpiresAt: expiresAt}
	if err := tx.Complete(r.Context(), key, record); err != nil {
		a.writeAdminError(w, http.StatusServiceUnavailable, "service_unavailable")
		return
	}
	for name, values := range buffered.header {
		for _, value := range values {
			w.Header().Add(name, value)
		}
	}
	w.WriteHeader(buffered.status)
	_, _ = w.Write(buffered.body.Bytes())
}

func (a *api) allowAdmin(w http.ResponseWriter, r *http.Request) bool {
	allowed, retry := a.admin.allow(canonicalRemoteIP(r.RemoteAddr).String())
	if allowed {
		return true
	}
	w.Header().Set("Retry-After", strconv.Itoa(retry))
	a.writeAdminError(w, http.StatusTooManyRequests, "rate_limited")
	return false
}

func (a *api) listSoftware(w http.ResponseWriter, r *http.Request) {
	page, pageSize, ok := a.adminPage(r)
	if !ok {
		a.writeAdminError(w, http.StatusBadRequest, "invalid_request")
		return
	}
	items, total, err := a.options.AdminService.ListSoftware(r.Context(), page, pageSize)
	if err != nil {
		a.writeAdminServiceError(w, err)
		return
	}
	a.writeAdminJSON(w, http.StatusOK, struct {
		Items    []model.Software `json:"items"`
		Page     int              `json:"page"`
		PageSize int              `json:"page_size"`
		Total    int              `json:"total"`
	}{items, page, pageSize, total})
}

func (a *api) createSoftware(w http.ResponseWriter, r *http.Request) {
	var request adminNameRequest
	if a.decodeRequest(w, r, &request, adminNameFields) != nil {
		a.writeAdminError(w, http.StatusBadRequest, "invalid_request")
		return
	}
	item, err := a.options.AdminService.CreateSoftware(r.Context(), request.Name)
	if err != nil {
		a.writeAdminServiceError(w, err)
		return
	}
	a.writeAdminJSON(w, http.StatusCreated, item)
}

func (a *api) renameSoftware(w http.ResponseWriter, r *http.Request) {
	var request adminNameRequest
	if a.decodeRequest(w, r, &request, adminNameFields) != nil {
		a.writeAdminError(w, http.StatusBadRequest, "invalid_request")
		return
	}
	if err := a.options.AdminService.RenameSoftware(r.Context(), r.PathValue("id"), request.Name); err != nil {
		a.writeAdminServiceError(w, err)
		return
	}
	a.writeAdminJSON(w, http.StatusOK, struct {
		ID   string `json:"id"`
		Name string `json:"name"`
	}{r.PathValue("id"), request.Name})
}

func (a *api) enableSoftware(w http.ResponseWriter, r *http.Request) {
	a.setSoftwareEnabled(w, r, true)
}
func (a *api) disableSoftware(w http.ResponseWriter, r *http.Request) {
	a.setSoftwareEnabled(w, r, false)
}

func (a *api) setSoftwareEnabled(w http.ResponseWriter, r *http.Request, enabled bool) {
	var request struct{}
	if a.decodeRequest(w, r, &request, adminEmptyFields) != nil {
		a.writeAdminError(w, http.StatusBadRequest, "invalid_request")
		return
	}
	if err := a.options.AdminService.SetSoftwareEnabled(r.Context(), r.PathValue("id"), enabled); err != nil {
		a.writeAdminServiceError(w, err)
		return
	}
	a.writeAdminJSON(w, http.StatusOK, struct {
		ID      string `json:"id"`
		Enabled bool   `json:"enabled"`
	}{r.PathValue("id"), enabled})
}

func (a *api) idempotencyKey(w http.ResponseWriter, r *http.Request) (string, bool) {
	values := r.Header.Values("Idempotency-Key")
	if len(values) != 1 || strings.Contains(values[0], ",") {
		a.writeAdminError(w, http.StatusBadRequest, "invalid_idempotency_key")
		return "", false
	}
	parsed, err := uuid.Parse(values[0])
	if err != nil || parsed.String() != values[0] {
		a.writeAdminError(w, http.StatusBadRequest, "invalid_idempotency_key")
		return "", false
	}
	return values[0], true
}

func (a *api) listPackages(w http.ResponseWriter, r *http.Request) {
	page, pageSize, ok := a.adminPage(r)
	if !ok {
		a.writeAdminError(w, http.StatusBadRequest, "invalid_request")
		return
	}
	items, total, err := a.options.AdminService.ListPackages(r.Context(), page, pageSize)
	if err != nil {
		a.writeAdminServiceError(w, err)
		return
	}
	a.writeAdminJSON(w, http.StatusOK, struct {
		Items    []model.Package `json:"items"`
		Page     int             `json:"page"`
		PageSize int             `json:"page_size"`
		Total    int             `json:"total"`
	}{items, page, pageSize, total})
}

func (a *api) createPackage(w http.ResponseWriter, r *http.Request) {
	var request adminPackageRequest
	if a.decodeRequest(w, r, &request, adminPackageFields) != nil {
		a.writeAdminError(w, http.StatusBadRequest, "invalid_request")
		return
	}
	item, err := a.options.AdminService.CreatePackage(r.Context(), request.Name, request.SoftwareIDs)
	if err != nil {
		a.writeAdminServiceError(w, err)
		return
	}
	a.writeAdminJSON(w, http.StatusCreated, item)
}

func (a *api) renamePackage(w http.ResponseWriter, r *http.Request) {
	var request adminNameRequest
	if a.decodeRequest(w, r, &request, adminNameFields) != nil {
		a.writeAdminError(w, http.StatusBadRequest, "invalid_request")
		return
	}
	if err := a.options.AdminService.RenamePackage(r.Context(), r.PathValue("id"), request.Name); err != nil {
		a.writeAdminServiceError(w, err)
		return
	}
	a.writeAdminJSON(w, http.StatusOK, struct {
		ID   string `json:"id"`
		Name string `json:"name"`
	}{r.PathValue("id"), request.Name})
}

func (a *api) replacePackageMembers(w http.ResponseWriter, r *http.Request) {
	var request adminMembersRequest
	if a.decodeRequest(w, r, &request, adminMembersFields) != nil {
		a.writeAdminError(w, http.StatusBadRequest, "invalid_request")
		return
	}
	if err := a.options.AdminService.ReplacePackageMembers(r.Context(), r.PathValue("id"), request.SoftwareIDs); err != nil {
		a.writeAdminServiceError(w, err)
		return
	}
	a.writeAdminJSON(w, http.StatusOK, struct {
		ID          string   `json:"id"`
		SoftwareIDs []string `json:"software_ids"`
	}{r.PathValue("id"), request.SoftwareIDs})
}

func (a *api) packageImpact(w http.ResponseWriter, r *http.Request) {
	var request adminMembersRequest
	if a.decodeRequest(w, r, &request, adminMembersFields) != nil {
		a.writeAdminError(w, http.StatusBadRequest, "invalid_request")
		return
	}
	count, err := a.options.AdminService.PackageRemovalImpact(r.Context(), r.PathValue("id"), request.SoftwareIDs)
	if err != nil {
		a.writeAdminServiceError(w, err)
		return
	}
	a.writeAdminJSON(w, http.StatusOK, struct {
		Affected int `json:"affected_valid_cards"`
		Delay    int `json:"max_offline_lease_delay_minutes"`
	}{count, 30})
}

func (a *api) searchCards(w http.ResponseWriter, r *http.Request) {
	allowed := map[string]struct{}{"page": {}, "page_size": {}, "complete_card": {}, "prefix8": {}, "last4": {}, "note_contains": {}, "package_id": {}, "card_type": {}, "state": {}}
	if rejectUnknownOrDuplicateQuery(r, allowed) != nil {
		a.writeAdminError(w, http.StatusBadRequest, "invalid_request")
		return
	}
	page, pageSize, ok := a.paginationValues(r)
	if !ok {
		a.writeAdminError(w, http.StatusBadRequest, "invalid_request")
		return
	}
	q := r.URL.Query()
	filter := model.AdminCardFilter{Prefix8: q.Get("prefix8"), Last4: q.Get("last4"), NoteContains: q.Get("note_contains"), PackageID: q.Get("package_id"), CardType: q.Get("card_type"), State: model.CardState(q.Get("state"))}
	items, total, err := a.options.AdminService.SearchAdminCards(r.Context(), q.Get("complete_card"), filter, page, pageSize)
	if err != nil {
		a.writeAdminServiceError(w, err)
		return
	}
	a.writeAdminJSON(w, http.StatusOK, struct {
		Items    []model.AdminCardSummary `json:"items"`
		Page     int                      `json:"page"`
		PageSize int                      `json:"page_size"`
		Total    int                      `json:"total"`
	}{items, page, pageSize, total})
}

func (a *api) generateCards(w http.ResponseWriter, r *http.Request) {
	var request adminGenerateRequest
	if a.decodeRequest(w, r, &request, adminGenerateFields) != nil {
		a.writeAdminError(w, http.StatusBadRequest, "invalid_request")
		return
	}
	items, err := a.options.AdminService.GenerateCards(r.Context(), request.PackageID, request.DurationDays, request.Count, request.DefaultNote, request.PerCardNotes)
	if err != nil {
		a.writeAdminServiceError(w, err)
		return
	}
	a.writeAdminJSON(w, http.StatusCreated, struct {
		Items []service.GeneratedCard `json:"items"`
	}{items})
}

func (a *api) copyCard(w http.ResponseWriter, r *http.Request) {
	item, err := a.options.AdminService.CopyCard(r.Context(), r.PathValue("id"))
	if err != nil {
		a.writeAdminServiceError(w, err)
		return
	}
	a.writeAdminJSON(w, http.StatusOK, item)
}

func (a *api) exportCards(w http.ResponseWriter, r *http.Request) {
	var request adminExportRequest
	if a.decodeRequest(w, r, &request, adminExportFields) != nil {
		a.writeAdminError(w, http.StatusBadRequest, "invalid_request")
		return
	}
	items, err := a.options.AdminService.ExportCards(r.Context(), request.CardIDs, a.options.MaxExportCount)
	if err != nil {
		a.writeAdminServiceError(w, err)
		return
	}
	if err := service.AssociateAdminReplayCards(r.Context(), request.CardIDs); err != nil {
		a.writeAdminError(w, http.StatusServiceUnavailable, "service_unavailable")
		return
	}
	var output bytes.Buffer
	output.Write([]byte{0xef, 0xbb, 0xbf})
	writer := csv.NewWriter(&output)
	_ = writer.Write([]string{"完整卡密", "套餐", "卡类型", "天数", "状态", "到期时间", "绑定设备短号", "备注"})
	for _, item := range items {
		expiry := ""
		if item.Summary.ExpiresAt != nil {
			expiry = item.Summary.ExpiresAt.UTC().Format(time.RFC3339)
		}
		binding := ""
		if item.Summary.BindingShortID != nil {
			binding = *item.Summary.BindingShortID
		}
		note := ""
		if item.Summary.Note != nil {
			note = *item.Summary.Note
		}
		_ = writer.Write([]string{safeCSVCell(item.Plaintext), safeCSVCell(item.Summary.PackageName), safeCSVCell(item.Summary.CardType), fmt.Sprint(item.Summary.DurationDays), string(item.Summary.State), expiry, safeCSVCell(binding), safeCSVCell(note)})
	}
	writer.Flush()
	if writer.Error() != nil {
		a.writeAdminError(w, http.StatusServiceUnavailable, "service_unavailable")
		return
	}
	w.Header().Set("Content-Type", "text/csv; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("Content-Disposition", `attachment; filename="license-cards.csv"`)
	w.WriteHeader(http.StatusOK)
	_, _ = w.Write(output.Bytes())
}

func safeCSVCell(value string) string {
	if value != "" && strings.ContainsAny(value[:1], "=+-@\t\r") {
		return "'" + value
	}
	return value
}

func (a *api) remarkCard(w http.ResponseWriter, r *http.Request) {
	var request adminRemarkRequest
	if a.decodeRequest(w, r, &request, adminRemarkFields) != nil {
		a.writeAdminError(w, http.StatusBadRequest, "invalid_request")
		return
	}
	if err := a.options.AdminService.RemarkCard(r.Context(), r.PathValue("id"), request.Note); err != nil {
		a.writeAdminServiceError(w, err)
		return
	}
	a.writeAdminJSON(w, http.StatusOK, struct {
		ID string `json:"id"`
	}{r.PathValue("id")})
}
func (a *api) renewCard(w http.ResponseWriter, r *http.Request) {
	var request adminRenewRequest
	if a.decodeRequest(w, r, &request, adminRenewFields) != nil {
		a.writeAdminError(w, http.StatusBadRequest, "invalid_request")
		return
	}
	if err := a.options.AdminService.RenewCard(r.Context(), r.PathValue("id"), request.DurationDays); err != nil {
		a.writeAdminServiceError(w, err)
		return
	}
	a.writeAdminJSON(w, http.StatusOK, struct {
		ID string `json:"id"`
	}{r.PathValue("id")})
}
func (a *api) unbindCard(w http.ResponseWriter, r *http.Request) {
	a.emptyCardAction(w, r, a.options.AdminService.UnbindCard)
}
func (a *api) deleteCard(w http.ResponseWriter, r *http.Request) {
	a.emptyCardAction(w, r, a.options.AdminService.DeleteCard)
}
func (a *api) emptyCardAction(w http.ResponseWriter, r *http.Request, action func(context.Context, string) error) {
	var request struct{}
	if a.decodeRequest(w, r, &request, adminEmptyFields) != nil {
		a.writeAdminError(w, http.StatusBadRequest, "invalid_request")
		return
	}
	if err := action(r.Context(), r.PathValue("id")); err != nil {
		a.writeAdminServiceError(w, err)
		return
	}
	a.writeAdminJSON(w, http.StatusOK, struct {
		ID string `json:"id"`
	}{r.PathValue("id")})
}

func (a *api) listDevices(w http.ResponseWriter, r *http.Request) {
	page, pageSize, ok := a.adminPage(r)
	if !ok {
		a.writeAdminError(w, http.StatusBadRequest, "invalid_request")
		return
	}
	items, total, err := a.options.AdminService.ListDevices(r.Context(), page, pageSize)
	if err != nil {
		a.writeAdminServiceError(w, err)
		return
	}
	a.writeAdminJSON(w, http.StatusOK, struct {
		Items    []model.DeviceSummary `json:"items"`
		Page     int                   `json:"page"`
		PageSize int                   `json:"page_size"`
		Total    int                   `json:"total"`
	}{items, page, pageSize, total})
}
func (a *api) getDevice(w http.ResponseWriter, r *http.Request) {
	item, err := a.options.AdminService.GetDevice(r.Context(), r.PathValue("id"))
	if err != nil {
		a.writeAdminServiceError(w, err)
		return
	}
	a.writeAdminJSON(w, http.StatusOK, item)
}
func (a *api) unbindDevice(w http.ResponseWriter, r *http.Request) {
	a.emptyCardAction(w, r, a.options.AdminService.UnbindDevice)
}

func (a *api) adminPage(r *http.Request) (int, int, bool) {
	if err := rejectUnknownOrDuplicateQuery(r, map[string]struct{}{"page": {}, "page_size": {}}); err != nil {
		return 0, 0, false
	}
	return a.paginationValues(r)
}

func (a *api) paginationValues(r *http.Request) (int, int, bool) {
	page, pageSize := 1, a.options.MaxAdminPageSize
	var err error
	if text := r.URL.Query().Get("page"); text != "" {
		page, err = strconv.Atoi(text)
		if err != nil || page < 1 {
			return 0, 0, false
		}
	}
	if text := r.URL.Query().Get("page_size"); text != "" {
		pageSize, err = strconv.Atoi(text)
		if err != nil || pageSize < 1 || pageSize > a.options.MaxAdminPageSize {
			return 0, 0, false
		}
	}
	maxInt := int(^uint(0) >> 1)
	if page-1 > maxInt/pageSize {
		return 0, 0, false
	}
	return page, pageSize, true
}

func rejectUnknownOrDuplicateQuery(r *http.Request, allowed map[string]struct{}) error {
	for name, values := range r.URL.Query() {
		if _, ok := allowed[name]; !ok || len(values) != 1 {
			return errors.New("invalid query")
		}
	}
	return nil
}

func (a *api) writeAdminJSON(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(value)
}

func (a *api) writeAdminError(w http.ResponseWriter, status int, code string) {
	a.writeAdminJSON(w, status, struct {
		Code string `json:"code"`
	}{code})
}

func (a *api) writeAdminServiceError(w http.ResponseWriter, err error) {
	switch {
	case errors.Is(err, service.ErrInvalidRequest):
		a.writeAdminError(w, http.StatusBadRequest, "invalid_request")
	case errors.Is(err, service.ErrNotFound):
		a.writeAdminError(w, http.StatusNotFound, "not_found")
	case errors.Is(err, service.ErrConflict):
		a.writeAdminError(w, http.StatusConflict, "conflict")
	default:
		a.writeAdminError(w, http.StatusServiceUnavailable, "service_unavailable")
	}
}
