package httpapi

import (
	"errors"
	"net/http"

	"fanfh.cn/superpan/ota-admin/internal/session"
	"fanfh.cn/superpan/ota-admin/internal/upload"
)

func (router *Router) receiveUpload(response http.ResponseWriter, request *http.Request, principal session.Principal) {
	if router.uploads == nil || router.uploadEnvelopes == nil {
		writeError(response, http.StatusServiceUnavailable, "upload_unavailable")
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
	if err := router.uploadEnvelopes.AcceptEnvelope(request.Context(), principal.IDHash, envelope); err != nil {
		if cleanupErr := envelope.Cleanup(); cleanupErr != nil {
			writeError(response, http.StatusInternalServerError, "operation_failed")
			return
		}
		writeUploadError(response, err)
		return
	}
	writeJSON(response, http.StatusAccepted, map[string]any{
		"state":            "envelope_accepted",
		"sha256":           envelope.SHA256,
		"compressed_bytes": envelope.CompressedBytes,
		"expanded_bytes":   envelope.ExpandedBytes,
		"entry_count":      len(envelope.Entries),
	})
}

func writeUploadError(response http.ResponseWriter, err error) {
	switch {
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
