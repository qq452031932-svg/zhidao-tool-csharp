package httpapi

import (
	"errors"
	"net/http"
	"strings"

	"fanfh.cn/superpan/ota-admin/internal/candidate"
	"fanfh.cn/superpan/ota-admin/internal/deploy"
	"fanfh.cn/superpan/ota-admin/internal/release"
	"fanfh.cn/superpan/ota-admin/internal/security"
	"fanfh.cn/superpan/ota-admin/internal/session"
	"fanfh.cn/superpan/ota-admin/internal/store"
)

func (router *Router) releaseHistory(response http.ResponseWriter, request *http.Request) {
	if router.releaseOperations == nil {
		writeError(response, http.StatusServiceUnavailable, "history_unavailable")
		return
	}
	items, err := router.releaseOperations.ReleaseHistory(request.Context())
	if err != nil {
		writeError(response, http.StatusInternalServerError, "operation_failed")
		return
	}
	readOnly := false
	if reporter, ok := router.releaseOperations.(interface{ ReadOnlyError() error }); ok {
		readOnly = reporter.ReadOnlyError() != nil
	}
	writeJSON(response, http.StatusOK, map[string]any{"releases": items, "client_auto_downgrade": false, "read_only": readOnly})
}

func (router *Router) rollbackRelease(response http.ResponseWriter, request *http.Request, principal session.Principal) {
	operationID, ok := uniqueHeader(request, "Idempotency-Key")
	if !ok || !canonicalOperationID.MatchString(operationID) {
		writeCandidateError(response, http.StatusBadRequest, "invalid_request", "请求标识无效", operationID)
		return
	}
	if router.releaseOperations == nil {
		writeCandidateError(response, http.StatusServiceUnavailable, "publish_unavailable", "发布服务正在恢复，请稍后重试", operationID)
		return
	}
	releaseID := strings.TrimSuffix(strings.TrimPrefix(request.URL.Path, "/admin/api/releases/"), "/rollback")
	if !canonicalOperationID.MatchString(releaseID) || strings.Contains(releaseID, "/") {
		writeCandidateError(response, http.StatusBadRequest, "invalid_request", "版本标识无效", operationID)
		return
	}
	var input struct {
		Confirm         bool   `json:"confirm"`
		CurrentPassword string `json:"current_password"`
	}
	if err := decodeStrictJSON(request, router.maxJSONBytes, &input); err != nil {
		writeJSONDecodeError(response, err)
		return
	}
	if !input.Confirm {
		writeCandidateError(response, http.StatusBadRequest, "confirmation_required", "请确认回滚", operationID)
		return
	}
	if input.CurrentPassword == "" || len(input.CurrentPassword) > security.MaximumPasswordBytes {
		writeCandidateError(response, http.StatusBadRequest, "password_rejected", "管理员密码不正确", operationID)
		return
	}
	result, err := router.releaseOperations.RollbackForActiveSession(request.Context(), principal, releaseID, operationID, input.CurrentPassword)
	if err != nil {
		switch {
		case errors.Is(err, session.ErrPassword):
			writeCandidateError(response, http.StatusBadRequest, "password_rejected", "管理员密码不正确", operationID)
		case errors.Is(err, deploy.ErrReadOnly):
			writeCandidateError(response, http.StatusServiceUnavailable, "publish_unavailable", "发布服务正在恢复，请稍后重试", operationID)
		case errors.Is(err, store.ErrSessionInactive), errors.Is(err, session.ErrUnauthorized):
			writeCandidateError(response, http.StatusUnauthorized, "authentication_failed", "登录已失效", operationID)
		case errors.Is(err, store.ErrNotFound):
			writeCandidateError(response, http.StatusNotFound, "release_not_found", "未找到可回滚版本", operationID)
		case errors.Is(err, release.ErrSignature):
			writeCandidateError(response, http.StatusUnprocessableEntity, "signature_mismatch", "签名校验失败", operationID)
		case errors.Is(err, release.ErrManifest), errors.Is(err, release.ErrMetadata), errors.Is(err, release.ErrCatalog), errors.Is(err, release.ErrReleasesIndex), errors.Is(err, candidate.ErrDigestMismatch):
			writeCandidateError(response, http.StatusUnprocessableEntity, "release_validation_failed", "发布包校验失败", operationID)
		case errors.Is(err, store.ErrInvalidTransition):
			writeCandidateError(response, http.StatusConflict, "operation_conflict", "已有发布或回滚正在进行", operationID)
		default:
			writeCandidateError(response, http.StatusInternalServerError, "operation_failed", "操作失败", operationID)
		}
		return
	}
	writeJSON(response, http.StatusOK, result)
}
