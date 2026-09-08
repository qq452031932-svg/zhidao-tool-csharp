package licenseadmin

import (
	"bytes"
	"context"
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
	Page         Page
	CompleteCard string
	Prefix8      string
	Last4        string
	NoteContains string
	PackageID    string
	CardType     string
	State        string
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
func (c *Client) SearchCards(ctx context.Context, search CardSearch) (Result, error) {
	query, err := cardSearchQuery(search)
	if err != nil {
		return Result{}, err
	}
	encoded := query.Encode()
	if int64(len(encoded)) > c.maxRequestBytes {
		return Result{}, errInvalidInput
	}
	return c.request(ctx, http.MethodGet, "/internal/v1/cards?"+encoded, nil, Mutation{})
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
	request.Header.Set("Accept", "application/json, text/csv")
	if body != nil {
		request.Header.Set("Content-Type", "application/json")
		request.Header.Set("Idempotency-Key", mutation.IdempotencyKey)
		request.Header.Set("X-Admin-Actor", mutation.Actor)
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
	if response.StatusCode >= 500 || response.StatusCode >= 300 && response.StatusCode < 400 {
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
	contentDisposition := ""
	contentType = "application/json"
	if mediaType == "text/csv" {
		contentType = "text/csv; charset=utf-8"
	}
	if mediaType == "text/csv" && response.Header.Get("Content-Disposition") == `attachment; filename="license-cards.csv"` {
		contentDisposition = response.Header.Get("Content-Disposition")
	}
	return Result{StatusCode: response.StatusCode, ContentType: contentType, ContentDisposition: contentDisposition, Body: responseBody}, nil
}

func validMutation(m Mutation) bool {
	parsed, err := uuid.Parse(m.IdempotencyKey)
	return err == nil && parsed.String() == m.IdempotencyKey && m.Actor != "" && len(m.Actor) <= 128 && m.Actor == strings.TrimSpace(m.Actor) && !strings.ContainsAny(m.Actor, "\r\n")
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

func cardSearchQuery(search CardSearch) (url.Values, error) {
	query, err := pageQuery(search.Page)
	if err != nil {
		return nil, err
	}
	for name, value := range map[string]string{
		"complete_card": search.CompleteCard, "prefix8": search.Prefix8, "last4": search.Last4,
		"note_contains": search.NoteContains, "package_id": search.PackageID, "card_type": search.CardType, "state": search.State,
	} {
		if value != "" {
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
