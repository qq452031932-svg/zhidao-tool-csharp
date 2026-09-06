package httpapi

import (
	"context"
	"errors"
	"net/http"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/candidate"
	"fanfh.cn/superpan/ota-admin/internal/release"
	"fanfh.cn/superpan/ota-admin/internal/session"
	"fanfh.cn/superpan/ota-admin/internal/store"
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
	contentTypes := request.Header.Values("Content-Type")
	if len(contentTypes) != 1 || contentTypes[0] != "application/zip" {
		writeError(response, http.StatusUnsupportedMediaType, "invalid_request")
		return
	}
	reserved, created, err := router.uploadEnvelopes.ReserveEnvelopeForActiveSession(request.Context(), principal.IDHash, operationID)
	if err != nil {
		writeUploadOperationError(response, err, operationID)
		return
	}
	if !created {
		writeReservedCandidateResult(response, reserved)
		return
	}
	leaseOwner := reserved.LeaseOwner
	receiveContext, stopLease := router.maintainUploadLease(request.Context(), principal.IDHash, operationID, leaseOwner)
	defer stopLease()
	envelope, err := router.uploads.Receive(receiveContext, request.Body, request.ContentLength)
	if err != nil {
		if !router.ensureUploadFailureRecorded(response, principal.IDHash, operationID, leaseOwner, uploadPublicFailureCode(err)) {
			return
		}
		writeUploadError(response, err)
		return
	}
	if envelope == nil || envelope.Verified {
		if envelope != nil {
			_ = envelope.Cleanup()
		}
		if !router.ensureUploadFailureRecorded(response, principal.IDHash, operationID, leaseOwner, "operation_failed") {
			return
		}
		writeError(response, http.StatusInternalServerError, "operation_failed")
		return
	}
	if err := router.uploadEnvelopes.RenewEnvelopeLeaseForActiveSession(request.Context(), principal.IDHash, operationID, leaseOwner); err != nil {
		_ = envelope.Cleanup()
		if !router.ensureUploadFailureRecorded(response, principal.IDHash, operationID, leaseOwner, "operation_failed") {
			return
		}
		writeUploadOperationError(response, err, operationID)
		return
	}
	currentPrincipal, err := router.sessions.Authenticate(request.Context(), sessionToken, csrfToken, true)
	if err != nil || currentPrincipal.IDHash != principal.IDHash || currentPrincipal.MustChangePassword {
		_ = envelope.Cleanup()
		if err == nil {
			err = session.ErrUnauthorized
		}
		if !router.ensureUploadFailureRecorded(response, principal.IDHash, operationID, leaseOwner, "operation_failed") {
			return
		}
		writeUploadError(response, err)
		return
	}
	summary, err := router.uploadEnvelopes.AcceptEnvelopeForActiveSession(request.Context(), currentPrincipal.IDHash, operationID, envelope, leaseOwner)
	if err != nil {
		_ = envelope.Cleanup()
		if !router.ensureUploadFailureRecorded(response, principal.IDHash, operationID, leaseOwner, candidatePublicFailureCode(err)) {
			return
		}
		writeUploadOperationError(response, err, operationID)
		return
	}
	writeReservedCandidateResult(response, summary)
}

func (router *Router) ensureUploadFailureRecorded(response http.ResponseWriter, sessionID, operationID, leaseOwner, code string) bool {
	finishContext, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	summary, err := router.uploadEnvelopes.FailEnvelopeForActiveSession(finishContext, sessionID, operationID, code, leaseOwner)
	if err == nil {
		return true
	}
	summary, found, lookupErr := router.uploadEnvelopes.CandidateForOperationForActiveSession(finishContext, sessionID, operationID)
	if lookupErr != nil || !found {
		writeCandidateError(response, http.StatusInternalServerError, "operation_failed", "操作失败，请重试", operationID)
		return false
	}
	if summary.State == string(store.CandidateUploading) {
		writeReservedCandidateResult(response, summary)
		return false
	}
	return true
}

func (router *Router) maintainUploadLease(parent context.Context, sessionID, operationID, leaseOwner string) (context.Context, func()) {
	ctx, cancel := context.WithCancel(parent)
	stop := make(chan struct{})
	done := make(chan struct{})
	go func() {
		defer close(done)
		ticker := time.NewTicker(time.Minute)
		defer ticker.Stop()
		for {
			select {
			case <-stop:
				return
			case <-ctx.Done():
				return
			case <-ticker.C:
				renewContext, renewCancel := context.WithTimeout(context.Background(), 5*time.Second)
				err := router.uploadEnvelopes.RenewEnvelopeLeaseForActiveSession(renewContext, sessionID, operationID, leaseOwner)
				renewCancel()
				if err != nil {
					cancel()
					return
				}
			}
		}
	}()
	return ctx, func() {
		close(stop)
		cancel()
		<-done
	}
}

func writeReservedCandidateResult(response http.ResponseWriter, summary candidate.Summary) {
	if summary.State != "failed" {
		if summary.State == "uploading" {
			response.Header().Set("Retry-After", "5")
			writeCandidateError(response, http.StatusConflict, "upload_in_progress", "上传处理中，请稍后重试", summary.OperationID)
			return
		}
		writeCandidateSummary(response, http.StatusAccepted, summary)
		return
	}
	switch summary.ResultCode {
	case "signature_mismatch":
		writeCandidateError(response, http.StatusUnprocessableEntity, "signature_mismatch", "签名校验失败", summary.OperationID)
	case "version_floor":
		writeCandidateError(response, http.StatusUnprocessableEntity, "version_floor", "版本号不是递增", summary.OperationID)
	case "hash_mismatch":
		writeCandidateError(response, http.StatusUnprocessableEntity, "release_validation_failed", "发布包校验失败", summary.OperationID)
	case "release_validation_failed":
		writeCandidateError(response, http.StatusUnprocessableEntity, "release_validation_failed", "发布包校验失败", summary.OperationID)
	case "invalid_zip_envelope":
		writeCandidateError(response, http.StatusUnprocessableEntity, "invalid_zip_envelope", "发布包格式无效", summary.OperationID)
	case "upload_too_large":
		writeCandidateError(response, http.StatusRequestEntityTooLarge, "upload_too_large", "发布包过大", summary.OperationID)
	case "insufficient_storage":
		writeCandidateError(response, http.StatusInsufficientStorage, "insufficient_storage", "服务器空间不足", summary.OperationID)
	case "expired", "candidate_expired":
		writeCandidateError(response, http.StatusGone, "candidate_expired", "候选版本已过期", summary.OperationID)
	default:
		writeCandidateError(response, http.StatusInternalServerError, "operation_failed", "操作失败", summary.OperationID)
	}
}

func candidatePublicFailureCode(err error) string {
	switch {
	case errors.Is(err, release.ErrSignature):
		return "signature_mismatch"
	case errors.Is(err, release.ErrVersionFloor):
		return "version_floor"
	case errors.Is(err, release.ErrManifest), errors.Is(err, release.ErrMetadata), errors.Is(err, release.ErrCatalog), errors.Is(err, release.ErrReleasesIndex), errors.Is(err, candidate.ErrDigestMismatch):
		return "release_validation_failed"
	case errors.Is(err, candidate.ErrExpired):
		return "candidate_expired"
	default:
		return "operation_failed"
	}
}

func uploadPublicFailureCode(err error) string {
	switch {
	case errors.Is(err, upload.ErrCompressedLimit):
		return "upload_too_large"
	case errors.Is(err, upload.ErrInsufficientDisk):
		return "insufficient_storage"
	case errors.Is(err, upload.ErrExpandedLimit), errors.Is(err, upload.ErrSingleFileLimit), errors.Is(err, upload.ErrEntryLimit), errors.Is(err, upload.ErrCompressionRatio), errors.Is(err, upload.ErrUnsafeName), errors.Is(err, upload.ErrUnknownName), errors.Is(err, upload.ErrDuplicateName), errors.Is(err, upload.ErrUnsafeEntry), errors.Is(err, upload.ErrInvalidArchive):
		return "invalid_zip_envelope"
	default:
		return "operation_failed"
	}
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
