package httpapi

import (
	"errors"
	"net/http"

	"fanfh.cn/superpan/ota-admin/internal/candidate"
	"fanfh.cn/superpan/ota-admin/internal/release"
	"fanfh.cn/superpan/ota-admin/internal/session"
	"fanfh.cn/superpan/ota-admin/internal/upload"
)

func (router *Router) receiveUpload(response http.ResponseWriter, request *http.Request, principal session.Principal, sessionToken, csrfToken string) {
	if router.uploads == nil || router.uploadEnvelopes == nil {
		writeError(response, http.StatusServiceUnavailable, "upload_unavailable")
		return
	}
	operationID, ok := uniqueHeader(request, "Idempotency-Key")
	if !ok || !canonicalOperationID.MatchString(operationID) {
		writeCandidateError(response, http.StatusBadRequest, "invalid_request", "请求标识无效", operationID)
		return
	}
	if existing, found, err := router.uploadEnvelopes.CandidateForOperationForActiveSession(request.Context(), principal.IDHash, operationID); err != nil {
		writeUploadOperationError(response, err, operationID)
		return
	} else if found {
		writeCandidateSummary(response, http.StatusAccepted, existing)
		return
	}
	contentTypes := request.Header.Values("Content-Type")
	if len(contentTypes) != 1 || contentTypes[0] != "application/zip" {
		writeError(response, http.StatusUnsupportedMediaType, "invalid_request")
		return
	}
	envelope, err := router.uploads.Receive(request.Context(), request.Body, request.ContentLength)
	if err != nil {
		writeUploadError(response, err)
		return
	}
	if envelope == nil || envelope.Verified {
		if envelope != nil {
			if err := envelope.Cleanup(); err != nil {
				writeError(response, http.StatusInternalServerError, "operation_failed")
				return
			}
		}
		writeError(response, http.StatusInternalServerError, "operation_failed")
		return
	}
	currentPrincipal, err := router.sessions.Authenticate(request.Context(), sessionToken, csrfToken, true)
	if err != nil || currentPrincipal.IDHash != principal.IDHash || currentPrincipal.MustChangePassword {
		if cleanupErr := envelope.Cleanup(); cleanupErr != nil {
			writeError(response, http.StatusInternalServerError, "operation_failed")
			return
		}
		if err == nil {
			err = session.ErrUnauthorized
		}
		writeUploadError(response, err)
		return
	}
	summary, err := router.uploadEnvelopes.AcceptEnvelopeForActiveSession(request.Context(), currentPrincipal.IDHash, operationID, envelope)
	if err != nil {
		if cleanupErr := envelope.Cleanup(); cleanupErr != nil {
			writeError(response, http.StatusInternalServerError, "operation_failed")
			return
		}
		writeUploadOperationError(response, err, operationID)
		return
	}
	writeCandidateSummary(response, http.StatusAccepted, summary)
}

func writeUploadOperationError(response http.ResponseWriter, err error, operationID string) {
	switch {
	case errors.Is(err, release.ErrSignature):
		writeCandidateError(response, http.StatusUnprocessableEntity, "signature_mismatch", "签名校验失败", operationID)
	case errors.Is(err, release.ErrVersionFloor):
		writeCandidateError(response, http.StatusUnprocessableEntity, "version_floor", "版本号不是递增", operationID)
	case errors.Is(err, release.ErrManifest), errors.Is(err, release.ErrMetadata), errors.Is(err, release.ErrCatalog), errors.Is(err, release.ErrReleasesIndex), errors.Is(err, candidate.ErrDigestMismatch):
		writeCandidateError(response, http.StatusUnprocessableEntity, "release_validation_failed", "发布包校验失败", operationID)
	case errors.Is(err, session.ErrUnauthorized):
		writeCandidateError(response, http.StatusUnauthorized, "authentication_failed", "登录已失效", operationID)
	case errors.Is(err, session.ErrForbidden):
		writeCandidateError(response, http.StatusForbidden, "request_rejected", "请求已拒绝", operationID)
	case errors.Is(err, candidate.ErrExpired):
		writeCandidateError(response, http.StatusGone, "candidate_expired", "候选版本已过期", operationID)
	default:
		writeCandidateError(response, http.StatusInternalServerError, "operation_failed", "操作失败", operationID)
	}
}

func writeUploadError(response http.ResponseWriter, err error) {
	switch {
	case errors.Is(err, session.ErrUnauthorized):
		writeError(response, http.StatusUnauthorized, "authentication_failed")
	case errors.Is(err, session.ErrForbidden):
		writeError(response, http.StatusForbidden, "request_rejected")
	case errors.Is(err, upload.ErrCompressedLimit):
		writeError(response, http.StatusRequestEntityTooLarge, "upload_too_large")
	case errors.Is(err, upload.ErrInsufficientDisk):
		writeError(response, http.StatusInsufficientStorage, "insufficient_storage")
	case errors.Is(err, upload.ErrExpandedLimit),
		errors.Is(err, upload.ErrSingleFileLimit),
		errors.Is(err, upload.ErrEntryLimit),
		errors.Is(err, upload.ErrCompressionRatio),
		errors.Is(err, upload.ErrUnsafeName),
		errors.Is(err, upload.ErrUnknownName),
		errors.Is(err, upload.ErrDuplicateName),
		errors.Is(err, upload.ErrUnsafeEntry),
		errors.Is(err, upload.ErrInvalidArchive):
		writeError(response, http.StatusUnprocessableEntity, "invalid_zip_envelope")
	default:
		writeError(response, http.StatusInternalServerError, "operation_failed")
	}
}
