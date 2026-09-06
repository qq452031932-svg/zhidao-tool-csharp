package httpapi

import (
	"errors"
	"net/http"
	"strings"

	"fanfh.cn/superpan/ota-admin/internal/candidate"
	"fanfh.cn/superpan/ota-admin/internal/session"
	"fanfh.cn/superpan/ota-admin/internal/store"
)

func (router *Router) currentCandidate(response http.ResponseWriter, request *http.Request, principal session.Principal) {
	if router.uploadEnvelopes == nil {
		writeCandidateError(response, http.StatusServiceUnavailable, "candidate_unavailable", "候选版本服务不可用", "")
		return
	}
	summary, err := router.uploadEnvelopes.CurrentForActiveSession(request.Context(), principal.IDHash)
	if errors.Is(err, store.ErrNotFound) {
		writeCandidateError(response, http.StatusNotFound, "not_found", "没有候选版本", "")
		return
	}
	if err != nil {
		writeUploadOperationError(response, err, "")
		return
	}
	writeCandidateSummary(response, http.StatusOK, summary)
}

func (router *Router) candidateStatus(response http.ResponseWriter, request *http.Request, principal session.Principal) {
	if router.uploadEnvelopes == nil {
		writeCandidateError(response, http.StatusServiceUnavailable, "candidate_unavailable", "候选版本服务不可用", "")
		return
	}
	id := strings.TrimPrefix(request.URL.Path, "/admin/api/candidates/")
	if !canonicalOperationID.MatchString(id) || strings.Contains(id, "/") {
		writeCandidateError(response, http.StatusNotFound, "not_found", "候选版本不存在", "")
		return
	}
	summary, err := router.uploadEnvelopes.StatusForActiveSession(request.Context(), principal.IDHash, id)
	if errors.Is(err, session.ErrUnauthorized) || errors.Is(err, store.ErrNotFound) {
		writeCandidateError(response, http.StatusNotFound, "not_found", "候选版本不存在", "")
		return
	}
	if err != nil {
		writeUploadOperationError(response, err, "")
		return
	}
	writeCandidateSummary(response, http.StatusOK, summary)
}

func writeCandidateSummary(response http.ResponseWriter, status int, summary candidate.Summary) {
	writeJSON(response, status, map[string]any{
		"candidate_id": summary.CandidateID,
		"operation_id": summary.OperationID,
		"state":        summary.State,
		"created_at":   summary.CreatedAt,
		"expires_at":   summary.ExpiresAt,
		"report": map[string]any{
			"package_id":            summary.Report.PackageID,
			"channel":               summary.Report.Channel,
			"version":               summary.Report.Version,
			"file_count":            summary.Report.FileCount,
			"verified":              summary.Report.Verified,
			"notes_markdown":        summary.Report.NotesMarkdown,
			"manifest_sha256":       summary.Report.ManifestSHA256,
			"publisher_spki_sha256": summary.Report.PublisherSPKISHA256,
		},
	})
}

func writeCandidateError(response http.ResponseWriter, status int, code, message, operationID string) {
	body := map[string]any{"code": code, "message": message}
	if canonicalOperationID.MatchString(operationID) {
		body["operation_id"] = operationID
	}
	writeJSON(response, status, body)
}
