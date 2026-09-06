package httpapi

import (
	"errors"
	"fmt"
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
			"gates":                 candidateGateEvidence(summary),
		},
	})
}

func candidateGateEvidence(summary candidate.Summary) map[string]map[string]any {
	report := summary.Report
	validated := summary.State == string(store.CandidateValidated) && report.Verified
	return map[string]map[string]any{
		"package":    {"passed": validated && report.PackageID != "", "evidence": fmt.Sprintf("包 ID：%s", report.PackageID)},
		"channel":    {"passed": validated && report.Channel != "", "evidence": fmt.Sprintf("渠道：%s", report.Channel)},
		"version":    {"passed": validated && report.Version != "", "evidence": fmt.Sprintf("候选版本：v%s，已通过历史版本门禁", report.Version)},
		"signature":  {"passed": validated && report.PublisherSPKISHA256 != "", "evidence": "发布清单签名与固定发布公钥匹配"},
		"files":      {"passed": validated && report.ManifestSHA256 != "" && report.FileCount > 0, "evidence": fmt.Sprintf("清单闭包与哈希一致，共 %d 个文件", report.FileCount)},
		"references": {"passed": validated, "evidence": "Feed、资产目录与 RELEASES 引用闭包一致"},
		"storage":    {"passed": validated, "evidence": "上传、解压与候选归档已安全落盘"},
	}
}

func writeCandidateError(response http.ResponseWriter, status int, code, message, operationID string) {
	body := map[string]any{"code": code, "message": message}
	if canonicalOperationID.MatchString(operationID) {
		body["operation_id"] = operationID
	}
	writeJSON(response, status, body)
}
