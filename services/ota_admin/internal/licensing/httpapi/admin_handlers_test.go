package httpapi

import (
	"bytes"
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/licensing/authwire"
	"fanfh.cn/superpan/ota-admin/internal/licensing/model"
	"fanfh.cn/superpan/ota-admin/internal/licensing/service"
	"fanfh.cn/superpan/ota-admin/internal/licensing/signing"
	licensestore "fanfh.cn/superpan/ota-admin/internal/licensing/store"
	"github.com/google/uuid"
)

const adminSecret = "0123456789abcdef0123456789abcdef"

var adminBearer = "Bearer " + authwire.EncodeInternalToken([]byte(adminSecret))

type adminHarness struct {
	t       *testing.T
	clock   *testClock
	store   *licensestore.Store
	service *service.Service
	handler http.Handler
	dbPath  string
}

func newAdminHarness(t *testing.T, limit int) *adminHarness {
	t.Helper()
	ctx := context.Background()
	dbPath := t.TempDir() + `\license.db`
	st, err := licensestore.Open(ctx, dbPath)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = st.Close() })
	clock := &testClock{now: time.Date(2026, 9, 9, 8, 0, 0, 0, time.UTC)}
	svc, handler := newAdminRouterForStore(t, st, clock, limit)
	return &adminHarness{t: t, clock: clock, store: st, service: svc, handler: handler, dbPath: dbPath}
}

func newAdminRouterForStore(t *testing.T, st *licensestore.Store, clock *testClock, limit int) (*service.Service, http.Handler) {
	return newAdminRouterForStoreWithSecret(t, st, clock, limit, []byte(adminSecret))
}

func newAdminRouterForStoreWithSecret(t *testing.T, st *licensestore.Store, clock *testClock, limit int, secret []byte) (*service.Service, http.Handler) {
	t.Helper()
	svc, err := service.New(st, clock, rand.Reader, bytes.Repeat([]byte{0x31}, 32), bytes.Repeat([]byte{0x32}, 32))
	if err != nil {
		t.Fatal(err)
	}
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	signer, err := signing.New("admin-test", key, rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	handler, err := NewRouter(Options{
		Service: svc, ReplayStore: st, Signer: signer, Clock: clock,
		MaxBodyBytes: 8192, MaxHeaderBytes: 4096,
		TimestampWindow: time.Minute, NonceLifetime: 2 * time.Minute, MaxReplayNonces: 100,
		ActivationRate: AntragRate(100), ValidationRate: AntragRate(100), MaxLimiterEntries: 32,
		AdminService: svc, AdminIdempotencyStore: st, InternalSecret: secret,
		AdminRate: RateLimit{Requests: limit, Window: time.Minute}, MaxAdminPageSize: 100, MaxExportCount: 100,
		MaxAdminIdempotencyEntries: 100, MaxAdminIdempotencyBytes: 1 << 20,
	})
	if err != nil {
		t.Fatal(err)
	}
	return svc, handler
}

func TestAdminSensitiveCardSearchUsesStrictReadOnlyPostBody(t *testing.T) {
	h := newAdminHarness(t, 100)
	const card = "ABCDEFGH-IJKLMNPQ-RSTUVWXY-Z234567A"
	const note = "客户备注 张三"
	body := `{"page":1,"page_size":20,"complete_card":"` + card + `","note_contains":"` + note + `"}`
	request := httptest.NewRequest(http.MethodPost, "/internal/v1/cards/search", strings.NewReader(body))
	request.RemoteAddr = "127.0.0.1:1"
	request.Header.Set("Authorization", adminBearer)
	request.Header.Set("Content-Type", "application/json")
	response := httptest.NewRecorder()
	h.handler.ServeHTTP(response, request)
	if response.Code != http.StatusOK {
		t.Fatalf("POST search status=%d body=%s", response.Code, response.Body.String())
	}
	if request.Header.Get("Idempotency-Key") != "" {
		t.Fatal("read-only search unexpectedly required idempotency")
	}
	if strings.Contains(request.URL.RequestURI(), card) || strings.Contains(request.URL.RequestURI(), note) {
		t.Fatal("sensitive values entered internal request URI")
	}

	get := httptest.NewRequest(http.MethodGet, "/internal/v1/cards?complete_card="+url.QueryEscape(card)+"&note_contains="+url.QueryEscape(note), nil)
	get.RemoteAddr = "127.0.0.1:1"
	get.Header.Set("Authorization", adminBearer)
	getResponse := httptest.NewRecorder()
	h.handler.ServeHTTP(getResponse, get)
	if getResponse.Code != http.StatusBadRequest {
		t.Fatalf("sensitive GET status=%d body=%s", getResponse.Code, getResponse.Body.String())
	}

	for name, invalidBody := range map[string]string{
		"unknown field":   `{"page":1,"page_size":20,"complete_card":"x","actor":"browser"}`,
		"duplicate field": `{"page":1,"page":2,"page_size":20}`,
		"overlong note":   `{"page":1,"page_size":20,"note_contains":"` + strings.Repeat("x", 1025) + `"}`,
	} {
		t.Run(name, func(t *testing.T) {
			r := httptest.NewRequest(http.MethodPost, "/internal/v1/cards/search", strings.NewReader(invalidBody))
			r.RemoteAddr = "127.0.0.1:2"
			r.Header.Set("Authorization", adminBearer)
			r.Header.Set("Content-Type", "application/json")
			w := httptest.NewRecorder()
			h.handler.ServeHTTP(w, r)
			if w.Code != http.StatusBadRequest {
				t.Fatalf("status=%d body=%s", w.Code, w.Body.String())
			}
		})
	}
}

func newAdminRouterWithEncryptionKeys(t *testing.T, st *licensestore.Store, clock *testClock, current []byte, previous [][]byte) (*service.Service, http.Handler) {
	return newAdminRouterWithCapacity(t, st, clock, current, previous, 100, 1<<20)
}

func newAdminRouterWithCapacity(t *testing.T, st *licensestore.Store, clock *testClock, current []byte, previous [][]byte, maxEntries int, maxBytes int64) (*service.Service, http.Handler) {
	t.Helper()
	svc, err := service.NewWithPreviousEncryptionKeys(st, clock, rand.Reader, current, previous, bytes.Repeat([]byte{0x32}, 32))
	if err != nil {
		t.Fatal(err)
	}
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	signer, err := signing.New("admin-test", key, rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	handler, err := NewRouter(Options{
		Service: svc, ReplayStore: st, Signer: signer, Clock: clock,
		MaxBodyBytes: 8192, MaxHeaderBytes: 4096,
		TimestampWindow: time.Minute, NonceLifetime: 2 * time.Minute, MaxReplayNonces: 100,
		ActivationRate: AntragRate(100), ValidationRate: AntragRate(100), MaxLimiterEntries: 32,
		AdminService: svc, AdminIdempotencyStore: st, InternalSecret: []byte(adminSecret),
		AdminRate: RateLimit{Requests: 100, Window: time.Minute}, MaxAdminPageSize: 100, MaxExportCount: 100,
		MaxAdminIdempotencyEntries: maxEntries, MaxAdminIdempotencyBytes: maxBytes,
	})
	if err != nil {
		t.Fatal(err)
	}
	return svc, handler
}

func TestAdminIdempotencyUsesDistinctEntryAndEncryptedByteCapacity(t *testing.T) {
	ctx := context.Background()
	clock := &testClock{now: time.Date(2026, 9, 9, 8, 0, 0, 0, time.UTC)}
	key := bytes.Repeat([]byte{0x31}, 32)

	entryStore, err := licensestore.Open(ctx, t.TempDir()+`\entries.db`)
	if err != nil {
		t.Fatal(err)
	}
	defer entryStore.Close()
	_, entryRouter := newAdminRouterWithCapacity(t, entryStore, clock, key, nil, 1, 1<<20)
	requestCreate := func(handler http.Handler, name string) *httptest.ResponseRecorder {
		r := httptest.NewRequest(http.MethodPost, "/internal/v1/software", strings.NewReader(`{"name":"`+name+`"}`))
		r.RemoteAddr = "127.0.0.1:10"
		r.Header.Set("Authorization", adminBearer)
		r.Header.Set("Content-Type", "application/json")
		r.Header.Set("Idempotency-Key", uuid.NewString())
		w := httptest.NewRecorder()
		handler.ServeHTTP(w, r)
		return w
	}
	if first := requestCreate(entryRouter, "Entry One"); first.Code != http.StatusCreated {
		t.Fatalf("first entry=%d %s", first.Code, first.Body.String())
	}
	if second := requestCreate(entryRouter, "Entry Two"); second.Code != http.StatusServiceUnavailable {
		t.Fatalf("entry capacity=%d %s", second.Code, second.Body.String())
	}
	if _, total, err := entryStore.ListSoftware(ctx, 0, 10); err != nil || total != 1 {
		t.Fatalf("entry capacity mutation total=%d err=%v", total, err)
	}

	byteStore, err := licensestore.Open(ctx, t.TempDir()+`\bytes.db`)
	if err != nil {
		t.Fatal(err)
	}
	defer byteStore.Close()
	_, byteRouter := newAdminRouterWithCapacity(t, byteStore, clock, key, nil, 100, 64)
	if response := requestCreate(byteRouter, "Byte Budget Product"); response.Code != http.StatusServiceUnavailable {
		t.Fatalf("byte capacity=%d %s", response.Code, response.Body.String())
	}
	if _, total, err := byteStore.ListSoftware(ctx, 0, 10); err != nil || total != 0 {
		t.Fatalf("oversize response committed mutation: total=%d err=%v", total, err)
	}
	software, err := byteStore.CreateSoftware(ctx, "Bounded Generate Product")
	if err != nil {
		t.Fatal(err)
	}
	pkg, err := byteStore.CreatePackage(ctx, "Bounded Generate Package", []string{software.ID})
	if err != nil {
		t.Fatal(err)
	}
	r := httptest.NewRequest(http.MethodPost, "/internal/v1/cards/generate", strings.NewReader(`{"package_id":"`+pkg.ID+`","duration_days":30,"count":2}`))
	r.RemoteAddr = "127.0.0.1:10"
	r.Header.Set("Authorization", adminBearer)
	r.Header.Set("Content-Type", "application/json")
	r.Header.Set("Idempotency-Key", uuid.NewString())
	w := httptest.NewRecorder()
	byteRouter.ServeHTTP(w, r)
	if w.Code != http.StatusServiceUnavailable {
		t.Fatalf("oversize generation=%d %s", w.Code, w.Body.String())
	}
	if _, total, err := byteStore.SearchAdminCards(ctx, model.AdminCardFilter{}, clock.Now(), 0, 10); err != nil || total != 0 {
		t.Fatalf("oversize generation committed cards: total=%d err=%v", total, err)
	}
}

func TestAdminExportGenerateAndPageBounds(t *testing.T) {
	h := newAdminHarness(t, 100)
	if response := h.request(http.MethodGet, "/internal/v1/cards?page=1&page_size=101", "", false); response.Code != http.StatusBadRequest {
		t.Fatalf("page bound=%d %s", response.Code, response.Body.String())
	}
	if response := h.request(http.MethodPost, "/internal/v1/cards/generate", `{"package_id":"`+uuid.NewString()+`","duration_days":30,"count":1001}`, true); response.Code != http.StatusBadRequest {
		t.Fatalf("generate bound=%d %s", response.Code, response.Body.String())
	}
	ids := make([]string, 101)
	for index := range ids {
		ids[index] = uuid.NewString()
	}
	body, err := json.Marshal(adminExportRequest{CardIDs: ids})
	if err != nil {
		t.Fatal(err)
	}
	if response := h.request(http.MethodPost, "/internal/v1/cards/export", string(body), true); response.Code != http.StatusBadRequest {
		t.Fatalf("export bound=%d %s", response.Code, response.Body.String())
	}
}

func TestAdminRawBinarySecretUsesCanonicalWireEncoding(t *testing.T) {
	ctx := context.Background()
	st, err := licensestore.Open(ctx, t.TempDir()+`\license.db`)
	if err != nil {
		t.Fatal(err)
	}
	defer st.Close()
	clock := &testClock{now: time.Date(2026, 9, 9, 8, 0, 0, 0, time.UTC)}
	secret := append([]byte{0x00, 0x0a, 0x0d, 0x7f, 0x80, 0xff}, bytes.Repeat([]byte{0x42}, 26)...)
	_, handler := newAdminRouterForStoreWithSecret(t, st, clock, 100, secret)
	request := func(authorization string) *httptest.ResponseRecorder {
		r := httptest.NewRequest(http.MethodGet, "/internal/v1/software", nil)
		r.RemoteAddr = "127.0.0.1:1"
		r.Header.Set("Authorization", authorization)
		w := httptest.NewRecorder()
		handler.ServeHTTP(w, r)
		return w
	}
	if got := request("Bearer " + authwire.EncodeInternalToken(secret)); got.Code != http.StatusOK {
		t.Fatalf("encoded secret status=%d body=%s", got.Code, got.Body.String())
	}
	if got := request("Bearer " + string(secret)); got.Code != http.StatusUnauthorized {
		t.Fatalf("raw binary secret status=%d body=%s", got.Code, got.Body.String())
	}
}

func AntragRate(requests int) RateLimit {
	return RateLimit{Requests: requests, Window: time.Minute}
}

func (h *adminHarness) request(method, path, body string, idempotency bool) *httptest.ResponseRecorder {
	h.t.Helper()
	r := httptest.NewRequest(method, path, strings.NewReader(body))
	r.RemoteAddr = "127.0.0.1:43000"
	r.Header.Set("Authorization", adminBearer)
	if body != "" {
		r.Header.Set("Content-Type", "application/json")
	}
	if idempotency {
		r.Header.Set("Idempotency-Key", uuid.NewString())
	}
	w := httptest.NewRecorder()
	h.handler.ServeHTTP(w, r)
	return w
}

func TestAdminRequiresImmediateLoopbackAndExactCanonicalBearer(t *testing.T) {
	h := newAdminHarness(t, 100)
	tests := []struct {
		name, remote string
		headers      []string
		want         int
	}{
		{name: "authorized IPv4 loopback", remote: "127.0.0.1:1", headers: []string{adminBearer}, want: http.StatusOK},
		{name: "authorized IPv6 loopback", remote: "[::1]:1", headers: []string{adminBearer}, want: http.StatusOK},
		{name: "non-loopback hidden despite forwarded loopback", remote: "198.51.100.2:1", headers: []string{adminBearer}, want: http.StatusNotFound},
		{name: "missing", remote: "127.0.0.1:1", want: http.StatusUnauthorized},
		{name: "wrong", remote: "127.0.0.1:1", headers: []string{"Bearer wrong"}, want: http.StatusUnauthorized},
		{name: "alternate scheme", remote: "127.0.0.1:1", headers: []string{"bearer " + adminSecret}, want: http.StatusUnauthorized},
		{name: "leading whitespace", remote: "127.0.0.1:1", headers: []string{" " + adminBearer}, want: http.StatusUnauthorized},
		{name: "trailing whitespace", remote: "127.0.0.1:1", headers: []string{adminBearer + " "}, want: http.StatusUnauthorized},
		{name: "comma", remote: "127.0.0.1:1", headers: []string{adminBearer + "," + adminBearer}, want: http.StatusUnauthorized},
		{name: "duplicate", remote: "127.0.0.1:1", headers: []string{adminBearer, adminBearer}, want: http.StatusUnauthorized},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			r := httptest.NewRequest(http.MethodGet, "/internal/v1/software", nil)
			r.RemoteAddr = tt.remote
			r.Header.Set("X-Real-IP", "127.0.0.1")
			for _, value := range tt.headers {
				r.Header.Add("Authorization", value)
			}
			w := httptest.NewRecorder()
			h.handler.ServeHTTP(w, r)
			if w.Code != tt.want {
				t.Fatalf("status=%d want=%d body=%s", w.Code, tt.want, w.Body.String())
			}
			if got := w.Header().Get("Access-Control-Allow-Origin"); got != "" {
				t.Fatalf("internal response has CORS header %q", got)
			}
			if (tt.want == http.StatusUnauthorized || tt.want == http.StatusNotFound) && strings.Contains(w.Body.String(), "secret") {
				t.Fatalf("generic rejection leaked detail: %s", w.Body.String())
			}
		})
	}
}

func TestAdminSoftwareLifecycleUsesImmutableIDAndHasNoDelete(t *testing.T) {
	h := newAdminHarness(t, 100)
	created := h.request(http.MethodPost, "/internal/v1/software", `{"name":"Product A"}`, true)
	if created.Code != http.StatusCreated {
		t.Fatalf("create status=%d body=%s", created.Code, created.Body.String())
	}
	var software struct {
		ID, Name string
		Enabled  bool
	}
	if err := json.Unmarshal(created.Body.Bytes(), &software); err != nil {
		t.Fatal(err)
	}
	if _, err := uuid.Parse(software.ID); err != nil || software.Name != "Product A" || !software.Enabled {
		t.Fatalf("created=%+v err=%v", software, err)
	}
	rename := h.request(http.MethodPatch, "/internal/v1/software/"+software.ID, `{"name":"Product B"}`, true)
	if rename.Code != http.StatusOK {
		t.Fatalf("rename status=%d body=%s", rename.Code, rename.Body.String())
	}
	disable := h.request(http.MethodPost, "/internal/v1/software/"+software.ID+"/disable", `{}`, true)
	if disable.Code != http.StatusOK {
		t.Fatalf("disable status=%d body=%s", disable.Code, disable.Body.String())
	}
	list := h.request(http.MethodGet, "/internal/v1/software?page=1&page_size=10", "", false)
	if list.Code != http.StatusOK || !strings.Contains(list.Body.String(), software.ID) || !strings.Contains(list.Body.String(), "Product B") || strings.Contains(list.Body.String(), `"enabled":true`) {
		t.Fatalf("list status=%d body=%s", list.Code, list.Body.String())
	}
	remove := h.request(http.MethodDelete, "/internal/v1/software/"+software.ID, "", true)
	if remove.Code != http.StatusMethodNotAllowed && remove.Code != http.StatusNotFound {
		t.Fatalf("software delete status=%d body=%s", remove.Code, remove.Body.String())
	}
}

func TestAdminMutationIdempotencyPersistsReplayAndRejectsKeyReuse(t *testing.T) {
	h := newAdminHarness(t, 100)
	key := uuid.NewString()
	do := func(name string) *httptest.ResponseRecorder {
		r := httptest.NewRequest(http.MethodPost, "/internal/v1/software", strings.NewReader(`{"name":"`+name+`"}`))
		r.RemoteAddr = "127.0.0.1:2"
		r.Header.Set("Authorization", adminBearer)
		r.Header.Set("Content-Type", "application/json")
		r.Header.Set("Idempotency-Key", key)
		w := httptest.NewRecorder()
		h.handler.ServeHTTP(w, r)
		return w
	}
	first := do("Replay Product")
	second := do("Replay Product")
	if first.Code != http.StatusCreated || second.Code != first.Code || second.Body.String() != first.Body.String() {
		t.Fatalf("replay differs: first=%d %s second=%d %s", first.Code, first.Body.String(), second.Code, second.Body.String())
	}
	conflict := do("Different Product")
	if conflict.Code != http.StatusConflict {
		t.Fatalf("conflicting reuse status=%d body=%s", conflict.Code, conflict.Body.String())
	}
	items, total, err := h.store.ListSoftware(context.Background(), 0, 100)
	if err != nil || total != 1 || len(items) != 1 {
		t.Fatalf("replay mutated twice: total=%d items=%d err=%v", total, len(items), err)
	}
}

type countedBody struct {
	reads atomic.Int64
}

func (b *countedBody) Read([]byte) (int, error) { b.reads.Add(1); return 0, context.Canceled }
func (b *countedBody) Close() error             { return nil }

func TestAdminLimiterIsSeparateResetsAndRejectsBeforeBodyRead(t *testing.T) {
	h := newAdminHarness(t, 1)
	first := h.request(http.MethodGet, "/internal/v1/software", "", false)
	if first.Code != http.StatusOK {
		t.Fatalf("first status=%d", first.Code)
	}
	body := &countedBody{}
	r := httptest.NewRequest(http.MethodPost, "/internal/v1/software", nil)
	r.Body = body
	r.RemoteAddr = "127.0.0.1:3"
	r.Header.Set("Authorization", adminBearer)
	r.Header.Set("Content-Type", "application/json")
	r.Header.Set("Idempotency-Key", uuid.NewString())
	w := httptest.NewRecorder()
	h.handler.ServeHTTP(w, r)
	if w.Code != http.StatusTooManyRequests || body.reads.Load() != 0 || w.Header().Get("Retry-After") == "" {
		t.Fatalf("limited status=%d reads=%d retry=%q body=%s", w.Code, body.reads.Load(), w.Header().Get("Retry-After"), w.Body.String())
	}
	h.clock.Advance(time.Minute)
	after := h.request(http.MethodGet, "/internal/v1/software", "", false)
	if after.Code != http.StatusOK {
		t.Fatalf("after reset status=%d body=%s", after.Code, after.Body.String())
	}
}

func TestAdminPackageLifecycleAndRemovalImpact(t *testing.T) {
	h := newAdminHarness(t, 100)
	softwareA, err := h.store.CreateSoftware(context.Background(), "Package A")
	if err != nil {
		t.Fatal(err)
	}
	softwareB, err := h.store.CreateSoftware(context.Background(), "Package B")
	if err != nil {
		t.Fatal(err)
	}
	created := h.request(http.MethodPost, "/internal/v1/packages", `{"name":"Suite","software_ids":["`+softwareA.ID+`","`+softwareB.ID+`"]}`, true)
	if created.Code != http.StatusCreated {
		t.Fatalf("create status=%d body=%s", created.Code, created.Body.String())
	}
	var pkg struct {
		ID string `json:"id"`
	}
	if err := json.Unmarshal(created.Body.Bytes(), &pkg); err != nil {
		t.Fatal(err)
	}
	impact := h.request(http.MethodPost, "/internal/v1/packages/"+pkg.ID+"/impact", `{"software_ids":["`+softwareB.ID+`"]}`, true)
	if impact.Code != http.StatusOK || !strings.Contains(impact.Body.String(), `"affected_valid_cards":0`) || !strings.Contains(impact.Body.String(), `"max_offline_lease_delay_minutes":30`) {
		t.Fatalf("impact status=%d body=%s", impact.Code, impact.Body.String())
	}
	replace := h.request(http.MethodPut, "/internal/v1/packages/"+pkg.ID+"/members", `{"software_ids":["`+softwareB.ID+`"]}`, true)
	if replace.Code != http.StatusOK {
		t.Fatalf("replace status=%d body=%s", replace.Code, replace.Body.String())
	}
	rename := h.request(http.MethodPatch, "/internal/v1/packages/"+pkg.ID, `{"name":"Renamed Suite"}`, true)
	if rename.Code != http.StatusOK {
		t.Fatalf("rename status=%d body=%s", rename.Code, rename.Body.String())
	}
	list := h.request(http.MethodGet, "/internal/v1/packages?page=1&page_size=10", "", false)
	if list.Code != http.StatusOK || !strings.Contains(list.Body.String(), "Renamed Suite") || strings.Contains(list.Body.String(), softwareA.ID) {
		t.Fatalf("list status=%d body=%s", list.Code, list.Body.String())
	}
}

func adminCardFixture(t *testing.T, h *adminHarness, note string) (string, string, string, string) {
	t.Helper()
	software, err := h.store.CreateSoftware(context.Background(), "Card Product "+uuid.NewString()[:8])
	if err != nil {
		t.Fatal(err)
	}
	pkg, err := h.store.CreatePackage(context.Background(), "Card Package "+uuid.NewString()[:8], []string{software.ID})
	if err != nil {
		t.Fatal(err)
	}
	cards, err := h.service.GenerateCards(context.Background(), pkg.ID, 30, 1, &note, nil)
	if err != nil {
		t.Fatal(err)
	}
	return software.ID, pkg.ID, cards[0].Card.ID, cards[0].Plaintext
}

func TestAdminCardGenerateSearchFiltersAndNormalRedaction(t *testing.T) {
	h := newAdminHarness(t, 100)
	software, pkg, cardID, plaintext := adminCardFixture(t, h, "Needle formula =SUM(A1:A2)")
	_ = software
	generated := h.request(http.MethodPost, "/internal/v1/cards/generate", `{"package_id":"`+pkg+`","duration_days":90,"count":1,"default_note":"batch","per_card_notes":["override"]}`, true)
	if generated.Code != http.StatusCreated || !strings.Contains(generated.Body.String(), `"plaintext"`) {
		t.Fatalf("generate status=%d body=%s", generated.Code, generated.Body.String())
	}
	prefix := strings.ReplaceAll(plaintext, "-", "")[:8]
	last4 := strings.ReplaceAll(plaintext, "-", "")[28:]
	queries := []string{
		"prefix8=" + prefix, "last4=" + last4, "package_id=" + pkg,
		"card_type=month", "state=unactivated",
	}
	for _, query := range queries {
		response := h.request(http.MethodGet, "/internal/v1/cards?page=1&page_size=20&"+query, "", false)
		if response.Code != http.StatusOK || !strings.Contains(response.Body.String(), cardID) {
			t.Fatalf("query %s status=%d body=%s", query, response.Code, response.Body.String())
		}
		assertNotContains(t, response.Body.String(), plaintext, "full_digest", "ciphertext", "machine_hash")
	}
	for name, body := range map[string]string{
		"note": `{"page":1,"page_size":20,"note_contains":"Needle"}`,
		"card": `{"page":1,"page_size":20,"complete_card":"` + plaintext + `"}`,
	} {
		response := h.request(http.MethodPost, "/internal/v1/cards/search", body, false)
		if response.Code != http.StatusOK || !strings.Contains(response.Body.String(), cardID) {
			t.Fatalf("sensitive %s status=%d body=%s", name, response.Code, response.Body.String())
		}
		assertNotContains(t, response.Body.String(), plaintext, "full_digest", "ciphertext", "machine_hash")
	}
	for _, query := range []string{"prefix8=TOOSHOR", "last4=123"} {
		response := h.request(http.MethodGet, "/internal/v1/cards?page=1&page_size=20&"+query, "", false)
		if response.Code != http.StatusBadRequest {
			t.Fatalf("partial exact filter %s status=%d body=%s", query, response.Code, response.Body.String())
		}
	}
}

func TestAdminCardTypeFiltersAndSummariesMapSupportedDurations(t *testing.T) {
	h := newAdminHarness(t, 100)
	software, err := h.store.CreateSoftware(context.Background(), "Card Type Product")
	if err != nil {
		t.Fatal(err)
	}
	pkg, err := h.store.CreatePackage(context.Background(), "Card Type Package", []string{software.ID})
	if err != nil {
		t.Fatal(err)
	}
	wants := []struct {
		days     int
		cardType string
	}{
		{days: 30, cardType: "month"},
		{days: 90, cardType: "quarter"},
		{days: 180, cardType: "half_year"},
		{days: 365, cardType: "year"},
	}
	for _, want := range wants {
		if _, err := h.service.GenerateCards(context.Background(), pkg.ID, want.days, 1, nil, nil); err != nil {
			t.Fatalf("generate %d day card: %v", want.days, err)
		}
	}
	for _, want := range wants {
		t.Run(want.cardType, func(t *testing.T) {
			response := h.request(http.MethodGet, "/internal/v1/cards?card_type="+want.cardType, "", false)
			if response.Code != http.StatusOK {
				t.Fatalf("status=%d body=%s", response.Code, response.Body.String())
			}
			var page struct {
				Items []model.AdminCardSummary `json:"items"`
			}
			if err := json.Unmarshal(response.Body.Bytes(), &page); err != nil {
				t.Fatal(err)
			}
			if len(page.Items) != 1 || page.Items[0].CardType != want.cardType || page.Items[0].DurationDays != want.days {
				t.Fatalf("items=%+v, want one %s/%d card", page.Items, want.cardType, want.days)
			}
		})
	}
	for _, cardType := range []string{"duration", "MONTH", "monthly", "30"} {
		invalid := h.request(http.MethodGet, "/internal/v1/cards?card_type="+cardType, "", false)
		if invalid.Code != http.StatusBadRequest {
			t.Fatalf("invalid card type %q status=%d body=%s", cardType, invalid.Code, invalid.Body.String())
		}
	}
}

func TestAdminCardCopyAndCSVExportAreExplicitAndFormulaSafe(t *testing.T) {
	h := newAdminHarness(t, 100)
	_, _, cardID, plaintext := adminCardFixture(t, h, "=FORMULA")
	copyResponse := h.request(http.MethodGet, "/internal/v1/cards/"+cardID+"/copy", "", false)
	if copyResponse.Code != http.StatusOK || !strings.Contains(copyResponse.Body.String(), plaintext) {
		t.Fatalf("copy status=%d body=%s", copyResponse.Code, copyResponse.Body.String())
	}
	exportKey := uuid.NewString()
	doExport := func() *httptest.ResponseRecorder {
		r := httptest.NewRequest(http.MethodPost, "/internal/v1/cards/export", strings.NewReader(`{"card_ids":["`+cardID+`"]}`))
		r.RemoteAddr = "127.0.0.1:7"
		r.Header.Set("Authorization", adminBearer)
		r.Header.Set("Content-Type", "application/json")
		r.Header.Set("Idempotency-Key", exportKey)
		w := httptest.NewRecorder()
		h.handler.ServeHTTP(w, r)
		return w
	}
	exported := doExport()
	if exported.Code != http.StatusOK {
		t.Fatalf("export status=%d body=%s", exported.Code, exported.Body.String())
	}
	data := exported.Body.Bytes()
	if !bytes.HasPrefix(data, []byte{0xef, 0xbb, 0xbf}) || !bytes.Contains(data, []byte("完整卡密,套餐,卡类型,天数,状态,到期时间,绑定设备短号,备注")) || !bytes.Contains(data, []byte(plaintext)) || !bytes.Contains(data, []byte("'=FORMULA")) {
		t.Fatalf("unsafe or incomplete CSV: %q", data)
	}
	replayed := doExport()
	if replayed.Code != exported.Code || replayed.Body.String() != exported.Body.String() || replayed.Header().Get("Content-Type") != "text/csv; charset=utf-8" {
		t.Fatalf("CSV replay differs: status=%d type=%q body=%q", replayed.Code, replayed.Header().Get("Content-Type"), replayed.Body.String())
	}
	record, found, err := h.store.GetAdminIdempotency(context.Background(), exportKey, h.clock.Now())
	if err != nil || !found {
		t.Fatalf("stored replay found=%v err=%v", found, err)
	}
	stored := record.Body
	if bytes.Contains(stored, []byte(plaintext)) || bytes.Contains(stored, []byte("=FORMULA")) {
		t.Fatalf("idempotency state stored plaintext card response: %q", stored)
	}
}

func TestAdminCardActionsAreIdempotentAndDeleteIsPermanent(t *testing.T) {
	h := newAdminHarness(t, 100)
	_, _, cardID, _ := adminCardFixture(t, h, "old")
	remark := h.request(http.MethodPatch, "/internal/v1/cards/"+cardID+"/remark", `{"note":"new"}`, true)
	if remark.Code != http.StatusOK {
		t.Fatalf("remark=%d %s", remark.Code, remark.Body.String())
	}
	renew := h.request(http.MethodPost, "/internal/v1/cards/"+cardID+"/renew", `{"duration_days":90}`, true)
	if renew.Code != http.StatusOK {
		t.Fatalf("renew=%d %s", renew.Code, renew.Body.String())
	}
	unbound := h.request(http.MethodPost, "/internal/v1/cards/"+cardID+"/unbind", `{}`, true)
	if unbound.Code != http.StatusOK {
		t.Fatalf("unbind=%d %s", unbound.Code, unbound.Body.String())
	}
	deleted := h.request(http.MethodDelete, "/internal/v1/cards/"+cardID, `{}`, true)
	if deleted.Code != http.StatusOK {
		t.Fatalf("delete=%d %s", deleted.Code, deleted.Body.String())
	}
	copyResponse := h.request(http.MethodGet, "/internal/v1/cards/"+cardID+"/copy", "", false)
	if copyResponse.Code != http.StatusNotFound {
		t.Fatalf("copy after delete=%d %s", copyResponse.Code, copyResponse.Body.String())
	}
	recycle := h.request(http.MethodPost, "/internal/v1/cards/"+cardID+"/restore", `{}`, true)
	if recycle.Code != http.StatusNotFound {
		t.Fatalf("restore route=%d %s", recycle.Code, recycle.Body.String())
	}
}

func TestAdminDeletePurgesGenerateAndExportPlaintextReplays(t *testing.T) {
	h := newAdminHarness(t, 100)
	software, err := h.store.CreateSoftware(context.Background(), "Replay Purge Product")
	if err != nil {
		t.Fatal(err)
	}
	pkg, err := h.store.CreatePackage(context.Background(), "Replay Purge Package", []string{software.ID})
	if err != nil {
		t.Fatal(err)
	}
	generateKey := uuid.NewString()
	do := func(method, path, body, key string) *httptest.ResponseRecorder {
		r := httptest.NewRequest(method, path, strings.NewReader(body))
		r.RemoteAddr = "127.0.0.1:8"
		r.Header.Set("Authorization", adminBearer)
		if body != "" {
			r.Header.Set("Content-Type", "application/json")
		}
		if key != "" {
			r.Header.Set("Idempotency-Key", key)
		}
		w := httptest.NewRecorder()
		h.handler.ServeHTTP(w, r)
		return w
	}
	generated := do(http.MethodPost, "/internal/v1/cards/generate", `{"package_id":"`+pkg.ID+`","duration_days":30,"count":1}`, generateKey)
	if generated.Code != http.StatusCreated {
		t.Fatalf("generate=%d %s", generated.Code, generated.Body.String())
	}
	var decoded struct {
		Items []struct {
			Card struct {
				ID string `json:"id"`
			} `json:"card"`
			Plaintext string `json:"plaintext"`
		} `json:"items"`
	}
	if err := json.Unmarshal(generated.Body.Bytes(), &decoded); err != nil || len(decoded.Items) != 1 {
		t.Fatalf("decode generated cards: %v body=%s", err, generated.Body.String())
	}
	cards := decoded.Items
	exportKey := uuid.NewString()
	exported := do(http.MethodPost, "/internal/v1/cards/export", `{"card_ids":["`+cards[0].Card.ID+`"]}`, exportKey)
	if exported.Code != http.StatusOK || !strings.Contains(exported.Body.String(), cards[0].Plaintext) {
		t.Fatalf("export=%d %s", exported.Code, exported.Body.String())
	}
	deleted := do(http.MethodDelete, "/internal/v1/cards/"+cards[0].Card.ID, `{}`, uuid.NewString())
	if deleted.Code != http.StatusOK {
		t.Fatalf("delete=%d %s", deleted.Code, deleted.Body.String())
	}
	exportReplay := do(http.MethodPost, "/internal/v1/cards/export", `{"card_ids":["`+cards[0].Card.ID+`"]}`, exportKey)
	if exportReplay.Code != http.StatusNotFound || strings.Contains(exportReplay.Body.String(), cards[0].Plaintext) {
		t.Fatalf("export replay recovered deleted plaintext: %d %s", exportReplay.Code, exportReplay.Body.String())
	}
	generateReplay := do(http.MethodPost, "/internal/v1/cards/generate", `{"package_id":"`+pkg.ID+`","duration_days":30,"count":1}`, generateKey)
	if generateReplay.Body.String() == generated.Body.String() || strings.Contains(generateReplay.Body.String(), cards[0].Plaintext) {
		t.Fatalf("generate replay recovered deleted plaintext: %d %s", generateReplay.Code, generateReplay.Body.String())
	}
}

func TestAdminDeviceSummariesMaskMachineAndCanUnbind(t *testing.T) {
	h := newAdminHarness(t, 100)
	software, _, cardID, plaintext := adminCardFixture(t, h, "device")
	rawMachine := "raw-machine-identifier-never-return"
	if _, err := h.service.Activate(context.Background(), "device-activation", plaintext, software, rawMachine); err != nil {
		t.Fatal(err)
	}
	devices := h.request(http.MethodGet, "/internal/v1/devices?page=1&page_size=20", "", false)
	if devices.Code != http.StatusOK || !strings.Contains(devices.Body.String(), cardID[:8]) || strings.Contains(devices.Body.String(), rawMachine) || strings.Contains(devices.Body.String(), `"machine_hash"`) {
		t.Fatalf("devices status=%d body=%s", devices.Code, devices.Body.String())
	}
	device := h.request(http.MethodGet, "/internal/v1/devices/"+cardID, "", false)
	if device.Code != http.StatusOK || !strings.Contains(device.Body.String(), cardID[:8]) || strings.Contains(device.Body.String(), rawMachine) {
		t.Fatalf("device read=%d %s", device.Code, device.Body.String())
	}
	unbound := h.request(http.MethodPost, "/internal/v1/devices/"+cardID+"/unbind", `{}`, true)
	if unbound.Code != http.StatusOK {
		t.Fatalf("device unbind=%d %s", unbound.Code, unbound.Body.String())
	}
	after := h.request(http.MethodGet, "/internal/v1/devices?page=1&page_size=20", "", false)
	if after.Code != http.StatusOK || strings.Contains(after.Body.String(), cardID[:8]) {
		t.Fatalf("devices after unbind=%d %s", after.Code, after.Body.String())
	}
}

func TestAdminStrictHeadersJSONAndUnknownRoutes(t *testing.T) {
	h := newAdminHarness(t, 100)
	tests := []struct {
		name   string
		mutate func(*http.Request)
		body   string
		want   int
	}{
		{name: "missing idempotency", want: http.StatusBadRequest},
		{name: "duplicate idempotency", mutate: func(r *http.Request) {
			r.Header.Add("Idempotency-Key", uuid.NewString())
			r.Header.Add("Idempotency-Key", uuid.NewString())
		}, want: http.StatusBadRequest},
		{name: "noncanonical idempotency", mutate: func(r *http.Request) { r.Header.Set("Idempotency-Key", strings.ToUpper(uuid.NewString())) }, want: http.StatusBadRequest},
		{name: "wrong media", mutate: func(r *http.Request) {
			r.Header.Set("Idempotency-Key", uuid.NewString())
			r.Header.Set("Content-Type", "text/plain")
		}, want: http.StatusBadRequest},
		{name: "duplicate JSON", body: `{"name":"A","name":"B"}`, mutate: func(r *http.Request) { r.Header.Set("Idempotency-Key", uuid.NewString()) }, want: http.StatusBadRequest},
		{name: "case variant JSON", body: `{"Name":"A"}`, mutate: func(r *http.Request) { r.Header.Set("Idempotency-Key", uuid.NewString()) }, want: http.StatusBadRequest},
		{name: "oversized headers", mutate: func(r *http.Request) {
			r.Header.Set("Idempotency-Key", uuid.NewString())
			r.Header.Set("X-Fill", strings.Repeat("x", 5000))
		}, want: http.StatusBadRequest},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			body := tt.body
			if body == "" {
				body = `{"name":"Strict Product"}`
			}
			r := httptest.NewRequest(http.MethodPost, "/internal/v1/software", strings.NewReader(body))
			r.RemoteAddr = "127.0.0.1:4"
			r.Header.Set("Authorization", adminBearer)
			r.Header.Set("Content-Type", "application/json")
			if tt.mutate != nil {
				tt.mutate(r)
			}
			w := httptest.NewRecorder()
			h.handler.ServeHTTP(w, r)
			if w.Code != tt.want {
				t.Fatalf("status=%d want=%d body=%s", w.Code, tt.want, w.Body.String())
			}
		})
	}
	r := httptest.NewRequest(http.MethodPost, "/internal/v1/cards/not-a-card/restore", strings.NewReader(`{}`))
	r.RemoteAddr = "127.0.0.1:5"
	r.Header.Set("Authorization", adminBearer)
	r.Header.Set("Content-Type", "application/json")
	w := httptest.NewRecorder()
	h.handler.ServeHTTP(w, r)
	if w.Code != http.StatusNotFound {
		t.Fatalf("unknown route without idempotency=%d body=%s", w.Code, w.Body.String())
	}
}

func TestAdminMalformedPathsAndMethodMismatchNeverRedirectOrReturnPlainText(t *testing.T) {
	h := newAdminHarness(t, 100)
	tests := []struct {
		name, method, path, authorization, remote, wantAllow string
		want                                                 int
	}{
		{name: "nonloopback traversal hidden", method: http.MethodGet, path: "/internal/v1/../software", authorization: adminBearer, remote: "198.51.100.8:1", want: http.StatusNotFound},
		{name: "bad auth repeated slash", method: http.MethodGet, path: "/internal//v1/software", authorization: "Bearer wrong", remote: "127.0.0.1:1", want: http.StatusUnauthorized},
		{name: "authorized traversal", method: http.MethodGet, path: "/internal/v1/../software", authorization: adminBearer, remote: "127.0.0.1:1", want: http.StatusNotFound},
		{name: "authorized repeated slash", method: http.MethodGet, path: "/internal//v1/software", authorization: adminBearer, remote: "127.0.0.1:1", want: http.StatusNotFound},
		{name: "method mismatch", method: http.MethodDelete, path: "/internal/v1/packages/" + uuid.NewString(), authorization: adminBearer, remote: "127.0.0.1:1", want: http.StatusMethodNotAllowed, wantAllow: http.MethodPatch},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			r := httptest.NewRequest(tt.method, tt.path, nil)
			r.RemoteAddr = tt.remote
			r.Header.Set("Authorization", tt.authorization)
			w := httptest.NewRecorder()
			h.handler.ServeHTTP(w, r)
			if w.Code != tt.want || w.Header().Get("Location") != "" || w.Header().Get("Cache-Control") != "no-store" || !strings.Contains(w.Header().Get("Content-Type"), "application/json") {
				t.Fatalf("status=%d want=%d location=%q type=%q cache=%q body=%q", w.Code, tt.want, w.Header().Get("Location"), w.Header().Get("Content-Type"), w.Header().Get("Cache-Control"), w.Body.String())
			}
			if got := w.Header().Get("Allow"); got != tt.wantAllow {
				t.Fatalf("Allow=%q want=%q", got, tt.wantAllow)
			}
		})
	}
}

func TestAdminRejectsPaginationOverflow(t *testing.T) {
	h := newAdminHarness(t, 100)
	response := h.request(http.MethodGet, "/internal/v1/software?page=9223372036854775807&page_size=100", "", false)
	if response.Code != http.StatusBadRequest {
		t.Fatalf("overflow page status=%d body=%s", response.Code, response.Body.String())
	}
}

func TestAdminCSVFormulaDefenseCoversEveryDangerousPrefix(t *testing.T) {
	for _, value := range []string{"=formula", "+formula", "-formula", "@formula", "\tformula", "\rformula"} {
		if got := safeCSVCell(value); got != "'"+value {
			t.Fatalf("safeCSVCell(%q)=%q", value, got)
		}
	}
}

func TestAdminConflictsAndConcurrentIdempotency(t *testing.T) {
	h := newAdminHarness(t, 100)
	secondStore, err := licensestore.Open(context.Background(), h.dbPath)
	if err != nil {
		t.Fatal(err)
	}
	defer secondStore.Close()
	_, secondHandler := newAdminRouterForStore(t, secondStore, h.clock, 100)
	first := h.request(http.MethodPost, "/internal/v1/software", `{"name":"Unique Product"}`, true)
	if first.Code != http.StatusCreated {
		t.Fatal(first.Body.String())
	}
	duplicate := h.request(http.MethodPost, "/internal/v1/software", `{"name":"Unique Product"}`, true)
	if duplicate.Code != http.StatusConflict {
		t.Fatalf("duplicate=%d body=%s", duplicate.Code, duplicate.Body.String())
	}
	key := uuid.NewString()
	const workers = 12
	results := make(chan *httptest.ResponseRecorder, workers)
	var wg sync.WaitGroup
	for i := 0; i < workers; i++ {
		wg.Add(1)
		go func(worker int) {
			defer wg.Done()
			r := httptest.NewRequest(http.MethodPost, "/internal/v1/software", strings.NewReader(`{"name":"Concurrent Admin Product"}`))
			r.RemoteAddr = "127.0.0.1:6"
			r.Header.Set("Authorization", adminBearer)
			r.Header.Set("Content-Type", "application/json")
			r.Header.Set("Idempotency-Key", key)
			w := httptest.NewRecorder()
			if worker%2 == 0 {
				h.handler.ServeHTTP(w, r)
			} else {
				secondHandler.ServeHTTP(w, r)
			}
			results <- w
		}(i)
	}
	wg.Wait()
	close(results)
	var body string
	for result := range results {
		if result.Code != http.StatusCreated {
			t.Fatalf("concurrent status=%d body=%s", result.Code, result.Body.String())
		}
		if body == "" {
			body = result.Body.String()
		} else if result.Body.String() != body {
			t.Fatalf("concurrent replay differs")
		}
	}
	items, total, err := h.store.ListSoftware(context.Background(), 0, 100)
	_ = items
	if err != nil || total != 2 {
		t.Fatalf("software total=%d err=%v", total, err)
	}
}

type lostResponseWriter struct {
	header http.Header
	status int
	body   bytes.Buffer
}

func (w *lostResponseWriter) Header() http.Header { return w.header }
func (w *lostResponseWriter) WriteHeader(status int) {
	if w.status == 0 {
		w.status = status
	}
}
func (w *lostResponseWriter) Write(p []byte) (int, error) {
	if w.status == 0 {
		w.status = http.StatusOK
	}
	_, _ = w.body.Write(p)
	return 0, errors.New("simulated client disconnect")
}

func TestAdminLostResponseAfterCommitReplaysExactlyAcrossRestart(t *testing.T) {
	ctx := context.Background()
	path := t.TempDir() + `\license.db`
	clock := &testClock{now: time.Date(2026, 9, 9, 8, 0, 0, 0, time.UTC)}
	firstStore, err := licensestore.Open(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	_, firstHandler := newAdminRouterForStore(t, firstStore, clock, 100)
	key := uuid.NewString()
	request := func() *http.Request {
		r := httptest.NewRequest(http.MethodPost, "/internal/v1/software", strings.NewReader(`{"name":"Restart Replay Product"}`))
		r.RemoteAddr = "127.0.0.1:7"
		r.Header.Set("Authorization", adminBearer)
		r.Header.Set("Content-Type", "application/json")
		r.Header.Set("Idempotency-Key", key)
		return r
	}
	lost := &lostResponseWriter{header: make(http.Header)}
	firstHandler.ServeHTTP(lost, request())
	if lost.status != http.StatusCreated {
		t.Fatalf("lost response status=%d body=%s", lost.status, lost.body.String())
	}
	if err := firstStore.Close(); err != nil {
		t.Fatal(err)
	}
	secondStore, err := licensestore.Open(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer secondStore.Close()
	_, secondHandler := newAdminRouterForStore(t, secondStore, clock, 100)
	replayed := httptest.NewRecorder()
	secondHandler.ServeHTTP(replayed, request())
	if replayed.Code != lost.status || replayed.Body.String() != lost.body.String() {
		t.Fatalf("restart replay differs: lost=%d %q replay=%d %q", lost.status, lost.body.String(), replayed.Code, replayed.Body.String())
	}
	_, total, err := secondStore.ListSoftware(ctx, 0, 10)
	if err != nil || total != 1 {
		t.Fatalf("lost response repeated mutation: total=%d err=%v", total, err)
	}
}

func TestAdminReplayEncryptionKeyRotationUsesBoundedPreviousKeysAndFailsClosed(t *testing.T) {
	ctx := context.Background()
	st, err := licensestore.Open(ctx, t.TempDir()+`\license.db`)
	if err != nil {
		t.Fatal(err)
	}
	defer st.Close()
	clock := &testClock{now: time.Date(2026, 9, 9, 8, 0, 0, 0, time.UTC)}
	oldKey := bytes.Repeat([]byte{0x41}, 32)
	newKey := bytes.Repeat([]byte{0x42}, 32)
	oldService, oldRouter := newAdminRouterWithEncryptionKeys(t, st, clock, oldKey, nil)
	software, err := st.CreateSoftware(ctx, "Rotated Card Product")
	if err != nil {
		t.Fatal(err)
	}
	pkg, err := st.CreatePackage(ctx, "Rotated Card Package", []string{software.ID})
	if err != nil {
		t.Fatal(err)
	}
	generated, err := oldService.GenerateCards(ctx, pkg.ID, 30, 1, nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	idempotencyKey := uuid.NewString()
	request := func(handler http.Handler) *httptest.ResponseRecorder {
		r := httptest.NewRequest(http.MethodPost, "/internal/v1/software", strings.NewReader(`{"name":"Rotated Replay Product"}`))
		r.RemoteAddr = "127.0.0.1:9"
		r.Header.Set("Authorization", adminBearer)
		r.Header.Set("Content-Type", "application/json")
		r.Header.Set("Idempotency-Key", idempotencyKey)
		w := httptest.NewRecorder()
		handler.ServeHTTP(w, r)
		return w
	}
	first := request(oldRouter)
	if first.Code != http.StatusCreated {
		t.Fatalf("old write=%d %s", first.Code, first.Body.String())
	}
	_, rotatedRouter := newAdminRouterWithEncryptionKeys(t, st, clock, newKey, [][]byte{oldKey})
	replayed := request(rotatedRouter)
	if replayed.Code != first.Code || replayed.Body.String() != first.Body.String() {
		t.Fatalf("rotation replay differs: first=%d %q replay=%d %q", first.Code, first.Body.String(), replayed.Code, replayed.Body.String())
	}
	copyRequest := func(handler http.Handler) *httptest.ResponseRecorder {
		r := httptest.NewRequest(http.MethodGet, "/internal/v1/cards/"+generated[0].Card.ID+"/copy", nil)
		r.RemoteAddr = "127.0.0.1:9"
		r.Header.Set("Authorization", adminBearer)
		w := httptest.NewRecorder()
		handler.ServeHTTP(w, r)
		return w
	}
	if copied := copyRequest(rotatedRouter); copied.Code != http.StatusOK || !strings.Contains(copied.Body.String(), generated[0].Plaintext) {
		t.Fatalf("rotated card copy=%d %s", copied.Code, copied.Body.String())
	}
	_, missingOldRouter := newAdminRouterWithEncryptionKeys(t, st, clock, newKey, nil)
	closed := request(missingOldRouter)
	if closed.Code != http.StatusServiceUnavailable || strings.Contains(closed.Body.String(), "key") {
		t.Fatalf("missing old key did not fail closed: %d %s", closed.Code, closed.Body.String())
	}
	if copied := copyRequest(missingOldRouter); copied.Code != http.StatusServiceUnavailable || strings.Contains(copied.Body.String(), "key") {
		t.Fatalf("missing old card key did not fail closed: %d %s", copied.Code, copied.Body.String())
	}
	if _, err := service.NewWithPreviousEncryptionKeys(st, clock, rand.Reader, newKey, [][]byte{oldKey, bytes.Repeat([]byte{0x43}, 32), bytes.Repeat([]byte{0x44}, 32)}, bytes.Repeat([]byte{0x32}, 32)); err == nil {
		t.Fatal("service accepted more than two previous encryption keys")
	}
}

func TestAdminImpactCountsEffectiveCardsAndStateUsesServerClock(t *testing.T) {
	h := newAdminHarness(t, 100)
	softwareA, err := h.store.CreateSoftware(context.Background(), "Impact A")
	if err != nil {
		t.Fatal(err)
	}
	softwareB, err := h.store.CreateSoftware(context.Background(), "Impact B")
	if err != nil {
		t.Fatal(err)
	}
	pkg, err := h.store.CreatePackage(context.Background(), "Impact Package", []string{softwareA.ID, softwareB.ID})
	if err != nil {
		t.Fatal(err)
	}
	cards, err := h.service.GenerateCards(context.Background(), pkg.ID, 30, 1, nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := h.service.Activate(context.Background(), "impact-activation", cards[0].Plaintext, softwareA.ID, "impact-machine"); err != nil {
		t.Fatal(err)
	}
	effective := h.request(http.MethodGet, "/internal/v1/cards?state=effective&page=1&page_size=10", "", false)
	if effective.Code != http.StatusOK || !strings.Contains(effective.Body.String(), cards[0].Card.ID) {
		t.Fatalf("effective=%d %s", effective.Code, effective.Body.String())
	}
	impact := h.request(http.MethodPost, "/internal/v1/packages/"+pkg.ID+"/impact", `{"software_ids":["`+softwareB.ID+`"]}`, true)
	if impact.Code != http.StatusOK || !strings.Contains(impact.Body.String(), `"affected_valid_cards":1`) {
		t.Fatalf("impact=%d %s", impact.Code, impact.Body.String())
	}
	h.clock.Advance(31 * 24 * time.Hour)
	expired := h.request(http.MethodGet, "/internal/v1/cards?state=expired&page=1&page_size=10", "", false)
	if expired.Code != http.StatusOK || !strings.Contains(expired.Body.String(), cards[0].Card.ID) {
		t.Fatalf("expired=%d %s", expired.Code, expired.Body.String())
	}
}

func TestAdminInvalidNotFoundAndConflictResponsesAreStable(t *testing.T) {
	h := newAdminHarness(t, 100)
	invalid := h.request(http.MethodPost, "/internal/v1/packages", `{"name":"Invalid Member","software_ids":["not-a-uuid"]}`, true)
	if invalid.Code != http.StatusBadRequest {
		t.Fatalf("invalid member=%d body=%s", invalid.Code, invalid.Body.String())
	}
	missing := h.request(http.MethodPost, "/internal/v1/packages", `{"name":"Missing Member","software_ids":["`+uuid.NewString()+`"]}`, true)
	if missing.Code != http.StatusNotFound {
		t.Fatalf("missing member=%d body=%s", missing.Code, missing.Body.String())
	}
	software, err := h.store.CreateSoftware(context.Background(), "Conflict Member")
	if err != nil {
		t.Fatal(err)
	}
	first := h.request(http.MethodPost, "/internal/v1/packages", `{"name":"Duplicate Package","software_ids":["`+software.ID+`"]}`, true)
	if first.Code != http.StatusCreated {
		t.Fatalf("first package=%d body=%s", first.Code, first.Body.String())
	}
	duplicate := h.request(http.MethodPost, "/internal/v1/packages", `{"name":"Duplicate Package","software_ids":["`+software.ID+`"]}`, true)
	if duplicate.Code != http.StatusConflict {
		t.Fatalf("duplicate package=%d body=%s", duplicate.Code, duplicate.Body.String())
	}
	var createdPackage struct {
		ID string `json:"id"`
	}
	if err := json.Unmarshal(first.Body.Bytes(), &createdPackage); err != nil {
		t.Fatal(err)
	}
	impactMissing := h.request(http.MethodPost, "/internal/v1/packages/"+createdPackage.ID+"/impact", `{"software_ids":["`+uuid.NewString()+`"]}`, true)
	if impactMissing.Code != http.StatusNotFound {
		t.Fatalf("impact missing member=%d body=%s", impactMissing.Code, impactMissing.Body.String())
	}
	badCopy := h.request(http.MethodGet, "/internal/v1/cards/not-a-uuid/copy", "", false)
	if badCopy.Code != http.StatusBadRequest {
		t.Fatalf("invalid card=%d body=%s", badCopy.Code, badCopy.Body.String())
	}
}
