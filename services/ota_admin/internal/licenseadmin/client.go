package licenseadmin

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"mime"
	"net"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/licensing/authwire"
	"github.com/google/uuid"
)

var (
	errUnavailable  = errors.New("license service unavailable")
	errInvalidInput = errors.New("invalid license admin request")
)

type Options struct {
	BaseURL          string
	Secret           []byte
	HTTPClient       *http.Client
	MaxRequestBytes  int64
	MaxResponseBytes int64
}

type Client struct {
	baseURL          string
	bearer           string
	httpClient       *http.Client
	maxRequestBytes  int64
	maxResponseBytes int64
}

type Page struct {
	Number int
	Size   int
}

type CardSearch struct {
	Page         Page   `json:"-"`
	CompleteCard string `json:"complete_card,omitempty"`
	Prefix8      string `json:"prefix8,omitempty"`
	Last4        string `json:"last4,omitempty"`
	NoteContains string `json:"note_contains,omitempty"`
	PackageID    string `json:"package_id,omitempty"`
	CardType     string `json:"card_type,omitempty"`
	State        string `json:"state,omitempty"`
}

type CardList struct {
	Page      Page
	Prefix8   string
	Last4     string
	PackageID string
	CardType  string
	State     string
}

type Mutation struct {
	Actor          string
	IdempotencyKey string
}

type Result struct {
	StatusCode         int
	ContentType        string
	ContentDisposition string
	Body               []byte
	RetryAfterSeconds  int
}

func NewClient(options Options) (*Client, error) {
	if err := validateLoopbackOrigin(options.BaseURL); err != nil {
		return nil, err
	}
	if len(options.Secret) != 32 || options.MaxRequestBytes < 1 || options.MaxResponseBytes < 1 {
		return nil, errInvalidInput
	}
	httpClient := options.HTTPClient
	if httpClient == nil {
		httpClient = &http.Client{Timeout: 10 * time.Second}
	}
	isolatedHTTPClient := *httpClient
	isolatedHTTPClient.CheckRedirect = func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }
	return &Client{
		baseURL: options.BaseURL, bearer: "Bearer " + authwire.EncodeInternalToken(options.Secret),
		httpClient: &isolatedHTTPClient, maxRequestBytes: options.MaxRequestBytes, maxResponseBytes: options.MaxResponseBytes,
	}, nil
}

func IsUnavailable(err error) bool { return errors.Is(err, errUnavailable) }

func (c *Client) ListSoftware(ctx context.Context, page Page) (Result, error) {
	return c.pageRequest(ctx, http.MethodGet, "/internal/v1/software", page)
}
func (c *Client) CreateSoftware(ctx context.Context, body []byte, mutation Mutation) (Result, error) {
	return c.mutate(ctx, http.MethodPost, "/internal/v1/software", body, mutation)
}
func (c *Client) RenameSoftware(ctx context.Context, id string, body []byte, mutation Mutation) (Result, error) {
	path, err := resourcePath("/internal/v1/software/", id, "")
	if err != nil {
		return Result{}, err
	}
	return c.mutate(ctx, http.MethodPatch, path, body, mutation)
}
func (c *Client) SetSoftwareEnabled(ctx context.Context, id string, enabled bool, body []byte, mutation Mutation) (Result, error) {
	action := "/disable"
	if enabled {
		action = "/enable"
	}
	path, err := resourcePath("/internal/v1/software/", id, action)
	if err != nil {
		return Result{}, err
	}
	return c.mutate(ctx, http.MethodPost, path, body, mutation)
}
func (c *Client) ListPackages(ctx context.Context, page Page) (Result, error) {
	return c.pageRequest(ctx, http.MethodGet, "/internal/v1/packages", page)
}
func (c *Client) CreatePackage(ctx context.Context, body []byte, mutation Mutation) (Result, error) {
	return c.mutate(ctx, http.MethodPost, "/internal/v1/packages", body, mutation)
}
func (c *Client) RenamePackage(ctx context.Context, id string, body []byte, mutation Mutation) (Result, error) {
	path, err := resourcePath("/internal/v1/packages/", id, "")
	if err != nil {
		return Result{}, err
	}
	return c.mutate(ctx, http.MethodPatch, path, body, mutation)
}
func (c *Client) ReplacePackageMembers(ctx context.Context, id string, body []byte, mutation Mutation) (Result, error) {
	path, err := resourcePath("/internal/v1/packages/", id, "/members")
	if err != nil {
		return Result{}, err
	}
	return c.mutate(ctx, http.MethodPut, path, body, mutation)
}
func (c *Client) PackageImpact(ctx context.Context, id string, body []byte, mutation Mutation) (Result, error) {
	path, err := resourcePath("/internal/v1/packages/", id, "/impact")
	if err != nil {
		return Result{}, err
	}
	return c.mutate(ctx, http.MethodPost, path, body, mutation)
}
func (c *Client) ListCards(ctx context.Context, list CardList) (Result, error) {
	query, err := cardListQuery(list)
	if err != nil {
		return Result{}, err
	}
	encoded := query.Encode()
	if int64(len(encoded)) > c.maxRequestBytes {
		return Result{}, errInvalidInput
	}
	return c.request(ctx, http.MethodGet, "/internal/v1/cards?"+encoded, nil, Mutation{})
}
func (c *Client) SearchCards(ctx context.Context, search CardSearch, actor string) (Result, error) {
	if _, err := pageQuery(search.Page); err != nil || !validActor(actor) {
		return Result{}, errInvalidInput
	}
	for _, field := range []struct {
		value   string
		maximum int
	}{
		{search.CompleteCard, 128}, {search.NoteContains, 1024}, {search.Prefix8, 128},
		{search.Last4, 128}, {search.PackageID, 128}, {search.CardType, 128}, {search.State, 128},
	} {
		if len(field.value) > field.maximum || strings.ContainsAny(field.value, "\r\n\x00") {
			return Result{}, errInvalidInput
		}
	}
	wire := struct {
		Page         int    `json:"page"`
		PageSize     int    `json:"page_size"`
		CompleteCard string `json:"complete_card,omitempty"`
		Prefix8      string `json:"prefix8,omitempty"`
		Last4        string `json:"last4,omitempty"`
		NoteContains string `json:"note_contains,omitempty"`
		PackageID    string `json:"package_id,omitempty"`
		CardType     string `json:"card_type,omitempty"`
		State        string `json:"state,omitempty"`
	}{search.Page.Number, search.Page.Size, search.CompleteCard, search.Prefix8, search.Last4, search.NoteContains, search.PackageID, search.CardType, search.State}
	body, err := json.Marshal(wire)
	if err != nil || int64(len(body)) > c.maxRequestBytes {
		return Result{}, errInvalidInput
	}
	return c.readOnlyPost(ctx, "/internal/v1/cards/search", body, actor)
}
func (c *Client) GenerateCards(ctx context.Context, body []byte, mutation Mutation) (Result, error) {
	return c.mutate(ctx, http.MethodPost, "/internal/v1/cards/generate", body, mutation)
}
func (c *Client) ExportCards(ctx context.Context, body []byte, mutation Mutation) (Result, error) {
	return c.mutate(ctx, http.MethodPost, "/internal/v1/cards/export", body, mutation)
}
func (c *Client) CopyCard(ctx context.Context, id string) (Result, error) {
	path, err := resourcePath("/internal/v1/cards/", id, "/copy")
	if err != nil {
		return Result{}, err
	}
	return c.request(ctx, http.MethodGet, path, nil, Mutation{})
}
func (c *Client) RemarkCard(ctx context.Context, id string, body []byte, mutation Mutation) (Result, error) {
	return c.cardMutation(ctx, http.MethodPatch, id, "/remark", body, mutation)
}
func (c *Client) RenewCard(ctx context.Context, id string, body []byte, mutation Mutation) (Result, error) {
	return c.cardMutation(ctx, http.MethodPost, id, "/renew", body, mutation)
}
func (c *Client) UnbindCard(ctx context.Context, id string, body []byte, mutation Mutation) (Result, error) {
	return c.cardMutation(ctx, http.MethodPost, id, "/unbind", body, mutation)
}
func (c *Client) DeleteCard(ctx context.Context, id string, body []byte, mutation Mutation) (Result, error) {
	return c.cardMutation(ctx, http.MethodDelete, id, "", body, mutation)
}
func (c *Client) ListDevices(ctx context.Context, page Page) (Result, error) {
	return c.pageRequest(ctx, http.MethodGet, "/internal/v1/devices", page)
}
func (c *Client) GetDevice(ctx context.Context, id string) (Result, error) {
	path, err := resourcePath("/internal/v1/devices/", id, "")
	if err != nil {
		return Result{}, err
	}
	return c.request(ctx, http.MethodGet, path, nil, Mutation{})
}
func (c *Client) UnbindDevice(ctx context.Context, id string, body []byte, mutation Mutation) (Result, error) {
	path, err := resourcePath("/internal/v1/devices/", id, "/unbind")
	if err != nil {
		return Result{}, err
	}
	return c.mutate(ctx, http.MethodPost, path, body, mutation)
}

func (c *Client) cardMutation(ctx context.Context, method, id, action string, body []byte, mutation Mutation) (Result, error) {
	path, err := resourcePath("/internal/v1/cards/", id, action)
	if err != nil {
		return Result{}, err
	}
	return c.mutate(ctx, method, path, body, mutation)
}

func (c *Client) pageRequest(ctx context.Context, method, path string, page Page) (Result, error) {
	query, err := pageQuery(page)
	if err != nil {
		return Result{}, err
	}
	return c.request(ctx, method, path+"?"+query.Encode(), nil, Mutation{})
}

func (c *Client) mutate(ctx context.Context, method, path string, body []byte, mutation Mutation) (Result, error) {
	if len(body) == 0 || int64(len(body)) > c.maxRequestBytes || !validMutation(mutation) {
		return Result{}, errInvalidInput
	}
	return c.request(ctx, method, path, body, mutation)
}

func (c *Client) readOnlyPost(ctx context.Context, path string, body []byte, actor string) (Result, error) {
	if len(body) == 0 || int64(len(body)) > c.maxRequestBytes || !validActor(actor) {
		return Result{}, errInvalidInput
	}
	return c.request(ctx, http.MethodPost, path, body, Mutation{Actor: actor})
}

func (c *Client) request(ctx context.Context, method, path string, body []byte, mutation Mutation) (Result, error) {
	if int64(len(path)) > c.maxRequestBytes {
		return Result{}, errInvalidInput
	}
	var reader io.Reader
	if body != nil {
		reader = bytes.NewReader(body)
	}
	request, err := http.NewRequestWithContext(ctx, method, c.baseURL+path, reader)
	if err != nil {
		return Result{}, errInvalidInput
	}
	request.Header.Set("Authorization", c.bearer)
	if method == http.MethodPost && strings.SplitN(path, "?", 2)[0] == "/internal/v1/cards/export" {
		request.Header.Set("Accept", "text/csv, application/json")
	} else {
		request.Header.Set("Accept", "application/json")
	}
	if body != nil {
		request.Header.Set("Content-Type", "application/json")
		request.Header.Set("X-Admin-Actor", mutation.Actor)
		if mutation.IdempotencyKey != "" {
			request.Header.Set("Idempotency-Key", mutation.IdempotencyKey)
		}
	}
	response, err := c.httpClient.Do(request)
	if err != nil {
		return Result{}, errUnavailable
	}
	defer response.Body.Close()
	limited := io.LimitReader(response.Body, c.maxResponseBytes+1)
	responseBody, err := io.ReadAll(limited)
	if err != nil || int64(len(responseBody)) > c.maxResponseBytes {
		return Result{}, errUnavailable
	}
	wireToken := strings.TrimPrefix(c.bearer, "Bearer ")
	if bytes.Contains(responseBody, []byte(c.bearer)) || bytes.Contains(responseBody, []byte(wireToken)) {
		return Result{}, errUnavailable
	}
	contentType := response.Header.Get("Content-Type")
	mediaType, _, mediaTypeErr := mime.ParseMediaType(contentType)
	if mediaTypeErr != nil || mediaType != "application/json" && mediaType != "text/csv" {
		return Result{}, errUnavailable
	}
	pathOnly := strings.SplitN(path, "?", 2)[0]
	isExport := method == http.MethodPost && pathOnly == "/internal/v1/cards/export"
	if !allowedResponseStatus(method, pathOnly, response.StatusCode) {
		return Result{}, errUnavailable
	}
	if isExport && response.StatusCode == http.StatusOK {
		if mediaType != "text/csv" {
			return Result{}, errUnavailable
		}
	} else if mediaType != "application/json" {
		return Result{}, errUnavailable
	}
	retryAfter := 0
	if response.StatusCode == http.StatusTooManyRequests {
		retryAfter, err = parseRetryAfter(response.Header)
		if err != nil {
			return Result{}, errUnavailable
		}
	}
	contentDisposition := ""
	contentType = "application/json"
	if mediaType == "text/csv" {
		contentType = "text/csv; charset=utf-8"
		if response.Header.Get("Content-Disposition") == `attachment; filename="license-cards.csv"` {
			contentDisposition = response.Header.Get("Content-Disposition")
		}
	}
	return Result{StatusCode: response.StatusCode, ContentType: contentType, ContentDisposition: contentDisposition, Body: responseBody, RetryAfterSeconds: retryAfter}, nil
}

func allowedResponseStatus(method, path string, status int) bool {
	if status == http.StatusTooManyRequests || status == http.StatusBadRequest {
		return true
	}
	isCollectionRead := method == http.MethodGet && (path == "/internal/v1/software" || path == "/internal/v1/packages" || path == "/internal/v1/cards" || path == "/internal/v1/devices")
	isSearch := method == http.MethodPost && path == "/internal/v1/cards/search"
	if status == http.StatusNotFound {
		return !isCollectionRead && !isSearch && !(method == http.MethodPost && path == "/internal/v1/software")
	}
	if status == http.StatusConflict {
		return method != http.MethodGet && !isSearch
	}
	if status == http.StatusCreated {
		return method == http.MethodPost && (path == "/internal/v1/software" || path == "/internal/v1/packages" || path == "/internal/v1/cards/generate")
	}
	if status != http.StatusOK {
		return false
	}
	return !(method == http.MethodPost && (path == "/internal/v1/software" || path == "/internal/v1/packages" || path == "/internal/v1/cards/generate"))
}

func parseRetryAfter(header http.Header) (int, error) {
	values := header.Values("Retry-After")
	if len(values) != 1 || values[0] == "" || strings.TrimSpace(values[0]) != values[0] {
		return 0, errUnavailable
	}
	for _, value := range values[0] {
		if value < '0' || value > '9' {
			return 0, errUnavailable
		}
	}
	seconds, err := strconv.Atoi(values[0])
	if err != nil || seconds < 1 || seconds > 3600 {
		return 0, errUnavailable
	}
	return seconds, nil
}

func validMutation(m Mutation) bool {
	parsed, err := uuid.Parse(m.IdempotencyKey)
	return err == nil && parsed.String() == m.IdempotencyKey && validActor(m.Actor)
}

func validActor(actor string) bool {
	return actor != "" && len(actor) <= 128 && actor == strings.TrimSpace(actor) && !strings.ContainsAny(actor, "\r\n")
}

func resourcePath(prefix, id, suffix string) (string, error) {
	parsed, err := uuid.Parse(id)
	if err != nil || parsed.String() != id {
		return "", errInvalidInput
	}
	return prefix + id + suffix, nil
}

func pageQuery(page Page) (url.Values, error) {
	if page.Number < 1 || page.Size < 1 || page.Size > 1000 {
		return nil, errInvalidInput
	}
	return url.Values{"page": {strconv.Itoa(page.Number)}, "page_size": {strconv.Itoa(page.Size)}}, nil
}

func cardListQuery(list CardList) (url.Values, error) {
	query, err := pageQuery(list.Page)
	if err != nil {
		return nil, err
	}
	for name, value := range map[string]string{
		"prefix8": list.Prefix8, "last4": list.Last4, "package_id": list.PackageID, "card_type": list.CardType, "state": list.State,
	} {
		if value != "" {
			if len(value) > 128 || value != strings.TrimSpace(value) || strings.ContainsAny(value, "\r\n\x00") {
				return nil, errInvalidInput
			}
			query.Set(name, value)
		}
	}
	return query, nil
}

func validateLoopbackOrigin(raw string) error {
	parsed, err := url.Parse(raw)
	if err != nil || parsed.Scheme != "http" || parsed.User != nil || parsed.Path != "" || parsed.RawPath != "" || parsed.RawQuery != "" || parsed.Fragment != "" || parsed.Opaque != "" || parsed.ForceQuery || parsed.String() != raw {
		return errInvalidInput
	}
	host := parsed.Hostname()
	port := parsed.Port()
	ip := net.ParseIP(host)
	portNumber, portErr := strconv.ParseUint(port, 10, 16)
	if ip == nil || !ip.IsLoopback() || portErr != nil || portNumber == 0 {
		return errInvalidInput
	}
	return nil
}
