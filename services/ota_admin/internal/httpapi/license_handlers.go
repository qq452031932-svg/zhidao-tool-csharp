package httpapi

import (
	"context"
	"errors"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"strings"

	"fanfh.cn/superpan/ota-admin/internal/licenseadmin"
	"fanfh.cn/superpan/ota-admin/internal/session"
)

const licenseAdminPrefix = "/admin/api/license"

func (router *Router) licenseAdmin(response http.ResponseWriter, request *http.Request, principal session.Principal) {
	if router.licenseAdminClient == nil {
		router.writeLicenseUnavailable(response)
		return
	}
	if malformedLicenseBrowserPath(request) {
		writeError(response, http.StatusNotFound, "not_found")
		return
	}
	path := strings.TrimPrefix(request.URL.Path, licenseAdminPrefix)
	segments := splitLicensePath(path)
	if segments == nil {
		writeError(response, http.StatusNotFound, "not_found")
		return
	}

	var (
		result licenseadmin.Result
		err    error
	)
	ctx := request.Context()
	switch {
	case len(segments) == 1 && segments[0] == "software" && request.Method == http.MethodGet:
		var page licenseadmin.Page
		page, err = licensePage(request.URL.Query(), map[string]struct{}{})
		if err == nil {
			result, err = router.licenseAdminClient.ListSoftware(ctx, page)
		}
	case len(segments) == 1 && segments[0] == "software" && request.Method == http.MethodPost:
		result, err = router.licenseMutation(response, request, principal, router.licenseAdminClient.CreateSoftware)
	case len(segments) == 2 && segments[0] == "software" && request.Method == http.MethodPatch:
		result, err = router.licenseResourceMutation(response, request, principal, segments[1], router.licenseAdminClient.RenameSoftware)
	case len(segments) == 3 && segments[0] == "software" && segments[2] == "enable" && request.Method == http.MethodPost:
		result, err = router.licenseSoftwareState(response, request, principal, segments[1], true)
	case len(segments) == 3 && segments[0] == "software" && segments[2] == "disable" && request.Method == http.MethodPost:
		result, err = router.licenseSoftwareState(response, request, principal, segments[1], false)
	case len(segments) == 1 && segments[0] == "packages" && request.Method == http.MethodGet:
		var page licenseadmin.Page
		page, err = licensePage(request.URL.Query(), map[string]struct{}{})
		if err == nil {
			result, err = router.licenseAdminClient.ListPackages(ctx, page)
		}
	case len(segments) == 1 && segments[0] == "packages" && request.Method == http.MethodPost:
		result, err = router.licenseMutation(response, request, principal, router.licenseAdminClient.CreatePackage)
	case len(segments) == 2 && segments[0] == "packages" && request.Method == http.MethodPatch:
		result, err = router.licenseResourceMutation(response, request, principal, segments[1], router.licenseAdminClient.RenamePackage)
	case len(segments) == 3 && segments[0] == "packages" && segments[2] == "members" && request.Method == http.MethodPut:
		result, err = router.licenseResourceMutation(response, request, principal, segments[1], router.licenseAdminClient.ReplacePackageMembers)
	case len(segments) == 3 && segments[0] == "packages" && segments[2] == "impact" && request.Method == http.MethodPost:
		result, err = router.licenseResourceMutation(response, request, principal, segments[1], router.licenseAdminClient.PackageImpact)
	case len(segments) == 1 && segments[0] == "cards" && request.Method == http.MethodGet:
		var list licenseadmin.CardList
		list, err = licenseCardList(request.URL.Query())
		if err == nil {
			result, err = router.licenseAdminClient.ListCards(ctx, list)
		}
	case len(segments) == 2 && segments[0] == "cards" && segments[1] == "search" && request.Method == http.MethodPost:
		if request.URL.RawQuery != "" || request.URL.ForceQuery {
			writeError(response, http.StatusBadRequest, "invalid_request")
			return
		}
		var search licenseadmin.CardSearch
		search, err = router.readLicenseCardSearch(response, request)
		if err == nil {
			result, err = router.licenseAdminClient.SearchCards(ctx, search, principal.IDHash)
		}
	case len(segments) == 2 && segments[0] == "cards" && segments[1] == "generate" && request.Method == http.MethodPost:
		result, err = router.licenseMutation(response, request, principal, router.licenseAdminClient.GenerateCards)
	case len(segments) == 2 && segments[0] == "cards" && segments[1] == "export" && request.Method == http.MethodPost:
		result, err = router.licenseMutation(response, request, principal, router.licenseAdminClient.ExportCards)
	case len(segments) == 3 && segments[0] == "cards" && segments[2] == "copy" && request.Method == http.MethodGet:
		result, err = router.licenseAdminClient.CopyCard(ctx, segments[1])
	case len(segments) == 3 && segments[0] == "cards" && segments[2] == "remark" && request.Method == http.MethodPatch:
		result, err = router.licenseResourceMutation(response, request, principal, segments[1], router.licenseAdminClient.RemarkCard)
	case len(segments) == 3 && segments[0] == "cards" && segments[2] == "renew" && request.Method == http.MethodPost:
		result, err = router.licenseResourceMutation(response, request, principal, segments[1], router.licenseAdminClient.RenewCard)
	case len(segments) == 3 && segments[0] == "cards" && segments[2] == "unbind" && request.Method == http.MethodPost:
		result, err = router.licenseResourceMutation(response, request, principal, segments[1], router.licenseAdminClient.UnbindCard)
	case len(segments) == 2 && segments[0] == "cards" && request.Method == http.MethodDelete:
		result, err = router.licenseResourceMutation(response, request, principal, segments[1], router.licenseAdminClient.DeleteCard)
	case len(segments) == 1 && segments[0] == "devices" && request.Method == http.MethodGet:
		var page licenseadmin.Page
		page, err = licensePage(request.URL.Query(), map[string]struct{}{})
		if err == nil {
			result, err = router.licenseAdminClient.ListDevices(ctx, page)
		}
	case len(segments) == 2 && segments[0] == "devices" && request.Method == http.MethodGet:
		result, err = router.licenseAdminClient.GetDevice(ctx, segments[1])
	case len(segments) == 3 && segments[0] == "devices" && segments[2] == "unbind" && request.Method == http.MethodPost:
		result, err = router.licenseResourceMutation(response, request, principal, segments[1], router.licenseAdminClient.UnbindDevice)
	default:
		if knownLicenseBrowserPath(segments) {
			writeMethodNotAllowed(response, allowedLicenseMethod(segments))
		} else {
			writeError(response, http.StatusNotFound, "not_found")
		}
		return
	}
	if err != nil {
		if errors.Is(err, errLicenseBodyAlreadyWritten) {
			return
		}
		if licenseadmin.IsUnavailable(err) {
			router.writeLicenseUnavailable(response)
		} else {
			writeError(response, http.StatusBadRequest, "invalid_request")
		}
		return
	}
	writeLicenseResult(response, result)
}

var errLicenseBodyAlreadyWritten = errors.New("license response already written")

type licenseMutationCall func(context.Context, []byte, licenseadmin.Mutation) (licenseadmin.Result, error)
type licenseResourceMutationCall func(context.Context, string, []byte, licenseadmin.Mutation) (licenseadmin.Result, error)

func (router *Router) licenseMutation(w http.ResponseWriter, r *http.Request, principal session.Principal, call licenseMutationCall) (licenseadmin.Result, error) {
	body, mutation, ok := router.readLicenseMutation(w, r, principal)
	if !ok {
		return licenseadmin.Result{}, errLicenseBodyAlreadyWritten
	}
	return call(r.Context(), body, mutation)
}

func (router *Router) licenseResourceMutation(w http.ResponseWriter, r *http.Request, principal session.Principal, id string, call licenseResourceMutationCall) (licenseadmin.Result, error) {
	body, mutation, ok := router.readLicenseMutation(w, r, principal)
	if !ok {
		return licenseadmin.Result{}, errLicenseBodyAlreadyWritten
	}
	return call(r.Context(), id, body, mutation)
}

func (router *Router) licenseSoftwareState(w http.ResponseWriter, r *http.Request, principal session.Principal, id string, enabled bool) (licenseadmin.Result, error) {
	body, mutation, ok := router.readLicenseMutation(w, r, principal)
	if !ok {
		return licenseadmin.Result{}, errLicenseBodyAlreadyWritten
	}
	return router.licenseAdminClient.SetSoftwareEnabled(r.Context(), id, enabled, body, mutation)
}

func (router *Router) readLicenseMutation(w http.ResponseWriter, r *http.Request, principal session.Principal) ([]byte, licenseadmin.Mutation, bool) {
	if r.ContentLength > router.maxJSONBytes {
		writeError(w, http.StatusRequestEntityTooLarge, "request_too_large")
		return nil, licenseadmin.Mutation{}, false
	}
	if values := r.Header.Values("Content-Type"); len(values) != 1 || values[0] != "application/json" {
		writeError(w, http.StatusUnsupportedMediaType, "invalid_request")
		return nil, licenseadmin.Mutation{}, false
	}
	keys := r.Header.Values("Idempotency-Key")
	if len(keys) != 1 || strings.Contains(keys[0], ",") {
		writeError(w, http.StatusBadRequest, "invalid_request")
		return nil, licenseadmin.Mutation{}, false
	}
	body, err := io.ReadAll(http.MaxBytesReader(w, r.Body, router.maxJSONBytes))
	if err != nil {
		var maximum *http.MaxBytesError
		if errors.As(err, &maximum) {
			writeError(w, http.StatusRequestEntityTooLarge, "request_too_large")
		} else {
			writeError(w, http.StatusBadRequest, "invalid_request")
		}
		return nil, licenseadmin.Mutation{}, false
	}
	if len(body) == 0 {
		writeError(w, http.StatusBadRequest, "invalid_request")
		return nil, licenseadmin.Mutation{}, false
	}
	return body, licenseadmin.Mutation{Actor: principal.IDHash, IdempotencyKey: keys[0]}, true
}

func writeLicenseResult(w http.ResponseWriter, result licenseadmin.Result) {
	w.Header().Set("Content-Type", result.ContentType)
	if result.RetryAfterSeconds > 0 {
		w.Header().Set("Retry-After", strconv.Itoa(result.RetryAfterSeconds))
	}
	if result.ContentDisposition != "" {
		w.Header().Set("Content-Disposition", result.ContentDisposition)
	}
	w.WriteHeader(result.StatusCode)
	_, _ = w.Write(result.Body)
}

func (router *Router) writeLicenseUnavailable(w http.ResponseWriter) {
	writeJSON(w, http.StatusServiceUnavailable, map[string]string{
		"code": "license_service_unavailable", "message": "授权服务暂时不可用，请稍后重试。",
	})
}

func malformedLicenseBrowserPath(r *http.Request) bool {
	path := r.URL.Path
	if r.URL.RawPath != "" || !strings.HasPrefix(path, licenseAdminPrefix+"/") || strings.Contains(path, "//") || strings.Contains(path, `\`) {
		return true
	}
	for _, part := range strings.Split(path, "/") {
		if part == "." || part == ".." {
			return true
		}
	}
	return false
}

func splitLicensePath(path string) []string {
	if path == "" || path == "/" || !strings.HasPrefix(path, "/") {
		return nil
	}
	parts := strings.Split(strings.TrimPrefix(path, "/"), "/")
	for _, part := range parts {
		if part == "" {
			return nil
		}
	}
	return parts
}

func licensePage(query url.Values, additional map[string]struct{}) (licenseadmin.Page, error) {
	allowed := map[string]struct{}{"page": {}, "page_size": {}}
	for key := range additional {
		allowed[key] = struct{}{}
	}
	for key, values := range query {
		if _, ok := allowed[key]; !ok || len(values) != 1 {
			return licenseadmin.Page{}, errors.New("invalid query")
		}
	}
	page, size := 1, 100
	var err error
	if value := query.Get("page"); value != "" {
		page, err = strconv.Atoi(value)
		if err != nil {
			return licenseadmin.Page{}, err
		}
	}
	if value := query.Get("page_size"); value != "" {
		size, err = strconv.Atoi(value)
		if err != nil {
			return licenseadmin.Page{}, err
		}
	}
	if page < 1 || size < 1 || size > 100 {
		return licenseadmin.Page{}, errors.New("invalid pagination")
	}
	return licenseadmin.Page{Number: page, Size: size}, nil
}

func licenseCardList(query url.Values) (licenseadmin.CardList, error) {
	additional := map[string]struct{}{"prefix8": {}, "last4": {}, "package_id": {}, "card_type": {}, "state": {}}
	page, err := licensePage(query, additional)
	if err != nil {
		return licenseadmin.CardList{}, err
	}
	return licenseadmin.CardList{Page: page, Prefix8: query.Get("prefix8"), Last4: query.Get("last4"), PackageID: query.Get("package_id"), CardType: query.Get("card_type"), State: query.Get("state")}, nil
}

type licenseCardSearchRequest struct {
	Page         int    `json:"page"`
	PageSize     int    `json:"page_size"`
	CompleteCard string `json:"complete_card"`
	Prefix8      string `json:"prefix8"`
	Last4        string `json:"last4"`
	NoteContains string `json:"note_contains"`
	PackageID    string `json:"package_id"`
	CardType     string `json:"card_type"`
	State        string `json:"state"`
}

func (router *Router) readLicenseCardSearch(w http.ResponseWriter, r *http.Request) (licenseadmin.CardSearch, error) {
	if len(r.Header.Values("Idempotency-Key")) != 0 {
		writeError(w, http.StatusBadRequest, "invalid_request")
		return licenseadmin.CardSearch{}, errLicenseBodyAlreadyWritten
	}
	var request licenseCardSearchRequest
	if err := decodeStrictJSON(r, router.maxJSONBytes, &request); err != nil {
		writeJSONDecodeError(w, err)
		return licenseadmin.CardSearch{}, errLicenseBodyAlreadyWritten
	}
	if request.Page < 1 || request.PageSize < 1 || request.PageSize > 100 || !validLicenseSearchText(request.CompleteCard, 128) || !validLicenseSearchText(request.NoteContains, 1024) {
		writeError(w, http.StatusBadRequest, "invalid_request")
		return licenseadmin.CardSearch{}, errLicenseBodyAlreadyWritten
	}
	for _, value := range []string{request.Prefix8, request.Last4, request.PackageID, request.CardType, request.State} {
		if !validLicenseSearchText(value, 128) {
			writeError(w, http.StatusBadRequest, "invalid_request")
			return licenseadmin.CardSearch{}, errLicenseBodyAlreadyWritten
		}
	}
	return licenseadmin.CardSearch{
		Page: licenseadmin.Page{Number: request.Page, Size: request.PageSize}, CompleteCard: request.CompleteCard,
		Prefix8: request.Prefix8, Last4: request.Last4, NoteContains: request.NoteContains,
		PackageID: request.PackageID, CardType: request.CardType, State: request.State,
	}, nil
}

func validLicenseSearchText(value string, maximum int) bool {
	return len(value) <= maximum && !strings.ContainsAny(value, "\r\n\x00")
}

func knownLicenseBrowserPath(parts []string) bool {
	if len(parts) == 1 {
		return parts[0] == "software" || parts[0] == "packages" || parts[0] == "cards" || parts[0] == "devices"
	}
	if len(parts) == 2 {
		if parts[0] == "cards" && parts[1] == "search" {
			return true
		}
		return parts[0] == "software" || parts[0] == "packages" || parts[0] == "cards" || parts[0] == "devices"
	}
	if len(parts) == 3 {
		return parts[0] == "software" || parts[0] == "packages" || parts[0] == "cards" || parts[0] == "devices"
	}
	return false
}

func allowedLicenseMethod(parts []string) string {
	if len(parts) == 2 && parts[0] == "cards" && parts[1] == "search" {
		return http.MethodPost
	}
	if len(parts) == 1 {
		if parts[0] == "software" || parts[0] == "packages" {
			return "GET, POST"
		}
		if parts[0] == "cards" || parts[0] == "devices" {
			return http.MethodGet
		}
	}
	return "GET, POST, PATCH, PUT, DELETE"
}
