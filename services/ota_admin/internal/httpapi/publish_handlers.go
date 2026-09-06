package httpapi

import (
	"errors"
	"net/http"
	"strings"

	"fanfh.cn/superpan/ota-admin/internal/candidate"
	"fanfh.cn/superpan/ota-admin/internal/deploy"
	"fanfh.cn/superpan/ota-admin/internal/release"
	"fanfh.cn/superpan/ota-admin/internal/session"
	"fanfh.cn/superpan/ota-admin/internal/store"
)

func (router *Router) publishCandidate(response http.ResponseWriter, request *http.Request, principal session.Principal) {
	if router.publisher == nil {
		writeError(response, http.StatusServiceUnavailable, "publish_unavailable")
		return
	}
	prefix := "/admin/api/candidates/"
	candidateID := strings.TrimSuffix(strings.TrimPrefix(request.URL.Path, prefix), "/publish")
	if candidateID == "" || strings.Contains(candidateID, "/") || !canonicalOperationID.MatchString(candidateID) {
		writeError(response, http.StatusBadRequest, "invalid_request")
		return
	}
	operationID, ok := uniqueHeader(request, "Idempotency-Key")
	if !ok || !canonicalOperationID.MatchString(operationID) {
		writeCandidateError(response, http.StatusBadRequest, "invalid_request", "请求标识无效", operationID)
		return
	}
	var body struct {
		Confirm bool `json:"confirm"`
	}
	if err := decodeStrictJSON(request, router.maxJSONBytes, &body); err != nil {
		writeJSONDecodeError(response, err)
		return
	}
	if !body.Confirm {
		writeCandidateError(response, http.StatusBadRequest, "confirmation_required", "请确认发布", operationID)
		return
	}
	result, err := router.publisher.PublishForActiveSession(request.Context(), principal.IDHash, candidateID, operationID)
	if err != nil {
		switch {
		case errors.Is(err, deploy.ErrReadOnly):
			writeCandidateError(response, http.StatusServiceUnavailable, "publish_unavailable", "发布服务正在恢复，请稍后重试", operationID)
		case errors.Is(err, session.ErrUnauthorized), errors.Is(err, store.ErrSessionInactive):
			writeCandidateError(response, http.StatusUnauthorized, "authentication_failed", "登录已失效", operationID)
		case errors.Is(err, candidate.ErrExpired), errors.Is(err, store.ErrCandidateExpired):
			writeCandidateError(response, http.StatusGone, "candidate_expired", "候选版本已过期", operationID)
		case errors.Is(err, release.ErrSignature):
			writeCandidateError(response, http.StatusUnprocessableEntity, "signature_mismatch", "签名校验失败", operationID)
		case errors.Is(err, release.ErrVersionFloor):
			writeCandidateError(response, http.StatusUnprocessableEntity, "version_floor", "版本号不是递增", operationID)
		case errors.Is(err, release.ErrManifest), errors.Is(err, release.ErrMetadata), errors.Is(err, release.ErrCatalog), errors.Is(err, release.ErrReleasesIndex), errors.Is(err, candidate.ErrDigestMismatch):
			writeCandidateError(response, http.StatusUnprocessableEntity, "release_validation_failed", "发布包校验失败", operationID)
		default:
			writeCandidateError(response, http.StatusInternalServerError, "operation_failed", "操作失败", operationID)
		}
		return
	}
	writeJSON(response, http.StatusOK, result)
}
