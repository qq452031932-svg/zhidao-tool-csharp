package service

import (
	"context"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"io"
	"reflect"
	"strings"
	"time"
	"unicode/utf8"

	"fanfh.cn/superpan/ota-admin/internal/licensing/model"
	licensestore "fanfh.cn/superpan/ota-admin/internal/licensing/store"
	"github.com/google/uuid"
)

var (
	ErrNotFound        = errors.New("license not found")
	ErrExpired         = errors.New("license expired")
	ErrAppNotAllowed   = errors.New("software not allowed")
	ErrAppDisabled     = errors.New("software disabled")
	ErrMachineMismatch = errors.New("machine mismatch")
	ErrInvalidRequest  = errors.New("invalid license request")
	ErrConflict        = errors.New("license state conflict")
)

const validationLease = 30 * time.Minute

const (
	MaxGenerateCount     = 1000
	MaxRawCardInputBytes = 256
	MaxNoteUTF8Bytes     = 512
)

type Clock interface {
	Now() time.Time
}

type Store interface {
	GenerateCards(context.Context, model.GenerateCardsRequest, []model.SealedCard) ([]model.Card, error)
	FindCardByDigest(context.Context, string) (model.Card, error)
	GetSoftwareAuthorization(context.Context, string, string) (model.Software, bool, error)
	GetPackageName(context.Context, string) (string, error)
	Activate(context.Context, model.ActivationMutation) (model.ActivationState, error)
	ValidateAndRecordLease(context.Context, model.Lease) (model.ActivationState, error)
	RenewCard(context.Context, string, int, time.Time) error
	UnbindCard(context.Context, string) error
	DeleteCard(context.Context, string) error
}

type AdminStore interface {
	ListSoftware(context.Context, int, int) ([]model.Software, int, error)
	CreateSoftware(context.Context, string) (model.Software, error)
	CreateSoftwareAt(context.Context, string, time.Time) (model.Software, error)
	RenameSoftware(context.Context, string, string) error
	SetSoftwareEnabled(context.Context, string, bool) error
	ListPackages(context.Context, int, int) ([]model.Package, int, error)
	CreatePackage(context.Context, string, []string) (model.Package, error)
	CreatePackageAt(context.Context, string, []string, time.Time) (model.Package, error)
	RenamePackage(context.Context, string, string) error
	ReplacePackageMembers(context.Context, string, []string) error
	PackageRemovalImpact(context.Context, string, []string, time.Time) (int, error)
	SearchAdminCards(context.Context, model.AdminCardFilter, time.Time, int, int) ([]model.AdminCardRecord, int, error)
	GetAdminCard(context.Context, string) (model.AdminCardRecord, error)
	GetAdminCards(context.Context, []string) ([]model.AdminCardRecord, error)
	SetCardNote(context.Context, string, *string) error
	ListDevices(context.Context, int, int) ([]model.DeviceRecord, int, error)
	GetDevice(context.Context, string) (model.DeviceRecord, error)
}

// AdminMutationTransaction is deliberately limited to license-database
// mutations. HTTP idempotency installs one in the request context so the
// business mutation and encrypted replay record share one SQLite transaction.
type AdminMutationTransaction interface {
	CreateSoftwareAt(context.Context, string, time.Time) (model.Software, error)
	RenameSoftware(context.Context, string, string) error
	SetSoftwareEnabled(context.Context, string, bool) error
	CreatePackageAt(context.Context, string, []string, time.Time) (model.Package, error)
	RenamePackage(context.Context, string, string) error
	ReplacePackageMembers(context.Context, string, []string) error
	GenerateCards(context.Context, model.GenerateCardsRequest, []model.SealedCard) ([]model.Card, error)
	SetCardNote(context.Context, string, *string) error
	RenewCard(context.Context, string, int, time.Time) error
	UnbindCard(context.Context, string) error
	DeleteCard(context.Context, string) error
}

type adminMutationTransactionKey struct{}

func WithAdminMutationTransaction(ctx context.Context, tx AdminMutationTransaction) context.Context {
	return context.WithValue(ctx, adminMutationTransactionKey{}, tx)
}

func adminMutationTransaction(ctx context.Context) (AdminMutationTransaction, bool) {
	tx, ok := ctx.Value(adminMutationTransactionKey{}).(AdminMutationTransaction)
	return tx, ok && !isNil(tx)
}

func (s *Service) ListPackages(ctx context.Context, page, pageSize int) ([]model.Package, int, error) {
	if page < 1 || pageSize < 1 {
		return nil, 0, ErrInvalidRequest
	}
	admin, ok := s.store.(AdminStore)
	if !ok {
		return nil, 0, errors.New("admin store unavailable")
	}
	items, total, err := admin.ListPackages(ctx, (page-1)*pageSize, pageSize)
	return items, total, classifyStoreError(err)
}

func (s *Service) CreatePackage(ctx context.Context, name string, softwareIDs []string) (model.Package, error) {
	if model.ValidatePackageName(name) != nil || !validUUIDIDs(softwareIDs, 100) {
		return model.Package{}, ErrInvalidRequest
	}
	now := s.clock.Now().UTC()
	if now.IsZero() {
		return model.Package{}, ErrInvalidRequest
	}
	var item model.Package
	var err error
	if tx, ok := adminMutationTransaction(ctx); ok {
		item, err = tx.CreatePackageAt(ctx, name, softwareIDs, now)
	} else if admin, ok := s.store.(AdminStore); ok {
		item, err = admin.CreatePackageAt(ctx, name, softwareIDs, now)
	} else {
		return model.Package{}, errors.New("admin store unavailable")
	}
	return item, classifyStoreError(err)
}

func (s *Service) RenamePackage(ctx context.Context, id, name string) error {
	if !validUUID(id) || model.ValidatePackageName(name) != nil {
		return ErrInvalidRequest
	}
	if tx, ok := adminMutationTransaction(ctx); ok {
		return classifyStoreError(tx.RenamePackage(ctx, id, name))
	}
	admin, ok := s.store.(AdminStore)
	if !ok {
		return errors.New("admin store unavailable")
	}
	return classifyStoreError(admin.RenamePackage(ctx, id, name))
}

func (s *Service) ReplacePackageMembers(ctx context.Context, id string, softwareIDs []string) error {
	if !validUUID(id) || !validUUIDIDs(softwareIDs, 100) {
		return ErrInvalidRequest
	}
	if tx, ok := adminMutationTransaction(ctx); ok {
		return classifyStoreError(tx.ReplacePackageMembers(ctx, id, softwareIDs))
	}
	admin, ok := s.store.(AdminStore)
	if !ok {
		return errors.New("admin store unavailable")
	}
	return classifyStoreError(admin.ReplacePackageMembers(ctx, id, softwareIDs))
}

func (s *Service) PackageRemovalImpact(ctx context.Context, id string, softwareIDs []string) (int, error) {
	if !validUUID(id) || !validUUIDIDs(softwareIDs, 100) {
		return 0, ErrInvalidRequest
	}
	now := s.clock.Now().UTC()
	if now.IsZero() {
		return 0, ErrInvalidRequest
	}
	admin, ok := s.store.(AdminStore)
	if !ok {
		return 0, errors.New("admin store unavailable")
	}
	count, err := admin.PackageRemovalImpact(ctx, id, softwareIDs, now)
	return count, classifyStoreError(err)
}

func (s *Service) SearchAdminCards(ctx context.Context, completeCard string, filter model.AdminCardFilter, page, pageSize int) ([]model.AdminCardSummary, int, error) {
	if page < 1 || pageSize < 1 || !validAdminCardFilter(filter) {
		return nil, 0, ErrInvalidRequest
	}
	if completeCard != "" {
		if !validRawCardInput(completeCard) {
			return nil, 0, ErrInvalidRequest
		}
		_, compact, err := normalizeCard(completeCard)
		if err != nil {
			return nil, 0, ErrInvalidRequest
		}
		filter.FullDigest = s.protector.digestCompact(compact)
	}
	now := s.clock.Now().UTC()
	if now.IsZero() {
		return nil, 0, ErrInvalidRequest
	}
	admin, ok := s.store.(AdminStore)
	if !ok {
		return nil, 0, errors.New("admin store unavailable")
	}
	records, total, err := admin.SearchAdminCards(ctx, filter, now, (page-1)*pageSize, pageSize)
	if err != nil {
		return nil, 0, classifyStoreError(err)
	}
	items := make([]model.AdminCardSummary, len(records))
	for i := range records {
		items[i] = adminCardSummary(records[i], now)
	}
	return items, total, nil
}

func (s *Service) CopyCard(ctx context.Context, id string) (model.AdminExportCard, error) {
	if !validUUID(id) {
		return model.AdminExportCard{}, ErrInvalidRequest
	}
	admin, ok := s.store.(AdminStore)
	if !ok {
		return model.AdminExportCard{}, errors.New("admin store unavailable")
	}
	record, err := admin.GetAdminCard(ctx, id)
	if err != nil {
		return model.AdminExportCard{}, classifyStoreError(err)
	}
	plaintext, err := s.protector.open(record.ID, record.Ciphertext, record.FullDigest)
	if err != nil {
		return model.AdminExportCard{}, errors.New("open protected card")
	}
	return model.AdminExportCard{Summary: adminCardSummary(record, s.clock.Now().UTC()), Plaintext: plaintext}, nil
}

func (s *Service) ExportCards(ctx context.Context, ids []string, maximum int) ([]model.AdminExportCard, error) {
	if len(ids) < 1 || len(ids) > maximum || maximum < 1 {
		return nil, ErrInvalidRequest
	}
	seen := make(map[string]struct{}, len(ids))
	for _, id := range ids {
		if !validUUID(id) {
			return nil, ErrInvalidRequest
		}
		if _, exists := seen[id]; exists {
			return nil, ErrInvalidRequest
		}
		seen[id] = struct{}{}
	}
	admin, ok := s.store.(AdminStore)
	if !ok {
		return nil, errors.New("admin store unavailable")
	}
	records, err := admin.GetAdminCards(ctx, ids)
	if err != nil {
		return nil, classifyStoreError(err)
	}
	now := s.clock.Now().UTC()
	result := make([]model.AdminExportCard, len(records))
	for i, record := range records {
		plaintext, err := s.protector.open(record.ID, record.Ciphertext, record.FullDigest)
		if err != nil {
			return nil, errors.New("open protected card")
		}
		result[i] = model.AdminExportCard{Summary: adminCardSummary(record, now), Plaintext: plaintext}
	}
	return result, nil
}

func (s *Service) RemarkCard(ctx context.Context, id string, note *string) error {
	if !validUUID(id) || !validNote(note) {
		return ErrInvalidRequest
	}
	if tx, ok := adminMutationTransaction(ctx); ok {
		return classifyStoreError(tx.SetCardNote(ctx, id, cloneString(note)))
	}
	admin, ok := s.store.(AdminStore)
	if !ok {
		return errors.New("admin store unavailable")
	}
	return classifyStoreError(admin.SetCardNote(ctx, id, cloneString(note)))
}

func (s *Service) ListDevices(ctx context.Context, page, pageSize int) ([]model.DeviceSummary, int, error) {
	if page < 1 || pageSize < 1 {
		return nil, 0, ErrInvalidRequest
	}
	admin, ok := s.store.(AdminStore)
	if !ok {
		return nil, 0, errors.New("admin store unavailable")
	}
	records, total, err := admin.ListDevices(ctx, (page-1)*pageSize, pageSize)
	if err != nil {
		return nil, 0, classifyStoreError(err)
	}
	items := make([]model.DeviceSummary, len(records))
	for i, record := range records {
		items[i] = deviceSummary(record)
	}
	return items, total, nil
}

func (s *Service) GetDevice(ctx context.Context, cardID string) (model.DeviceSummary, error) {
	if !validUUID(cardID) {
		return model.DeviceSummary{}, ErrInvalidRequest
	}
	admin, ok := s.store.(AdminStore)
	if !ok {
		return model.DeviceSummary{}, errors.New("admin store unavailable")
	}
	record, err := admin.GetDevice(ctx, cardID)
	if err != nil {
		return model.DeviceSummary{}, classifyStoreError(err)
	}
	return deviceSummary(record), nil
}

func (s *Service) UnbindDevice(ctx context.Context, cardID string) error {
	return s.UnbindCard(ctx, cardID)
}

func (s *Service) ProtectAdminReplay(key string, plaintext []byte) ([]byte, error) {
	if !validUUID(key) {
		return nil, ErrInvalidRequest
	}
	return s.protector.sealAdminReplay(key, plaintext)
}
func (s *Service) OpenAdminReplay(key string, ciphertext []byte) ([]byte, error) {
	if !validUUID(key) {
		return nil, ErrInvalidRequest
	}
	return s.protector.openAdminReplay(key, ciphertext)
}

func deviceSummary(record model.DeviceRecord) model.DeviceSummary {
	cardShort := record.CardID
	if len(cardShort) > 8 {
		cardShort = cardShort[:8]
	}
	return model.DeviceSummary{CardShortID: cardShort, MachineShortID: shortOpaque("machine:" + record.MachineHash), BoundAt: record.BoundAt, LastSuccessfulValidation: record.LastSeenAt, ExpiresAt: record.ExpiresAt}
}

func validAdminCardFilter(filter model.AdminCardFilter) bool {
	if (filter.Prefix8 != "" && len(filter.Prefix8) != 8) || (filter.Last4 != "" && len(filter.Last4) != 4) || len(filter.NoteContains) > MaxNoteUTF8Bytes || (filter.PackageID != "" && !validUUID(filter.PackageID)) || !utf8.ValidString(filter.NoteContains) {
		return false
	}
	if filter.CardType != "" && filter.CardType != "duration" {
		return false
	}
	switch filter.State {
	case "", model.CardStateUnactivated, model.CardStateEffective, model.CardStateExpired:
	default:
		return false
	}
	return true
}

func adminCardSummary(record model.AdminCardRecord, now time.Time) model.AdminCardSummary {
	state := model.CardStateUnactivated
	if record.ActivatedAt != nil {
		state = model.CardStateEffective
		if record.ExpiresAt != nil && !record.ExpiresAt.After(now) {
			state = model.CardStateExpired
		}
	}
	var binding *string
	if record.MachineHash != nil {
		value := shortOpaque("machine:" + *record.MachineHash)
		binding = &value
	}
	return model.AdminCardSummary{ID: record.ID, PackageID: record.PackageID, PackageName: record.PackageName, Prefix8: record.Prefix8, Last4: record.Last4, CardType: "duration", DurationDays: record.BaseDays, State: state, ActivatedAt: record.ActivatedAt, ExpiresAt: record.ExpiresAt, BindingShortID: binding, Note: cloneString(record.Note), CreatedAt: record.CreatedAt}
}

func shortOpaque(value string) string {
	sum := sha256.Sum256([]byte("license-admin-short:v1:" + value))
	return hex.EncodeToString(sum[:6])
}

var _ Store = (*licensestore.Store)(nil)

type Service struct {
	store     Store
	clock     Clock
	protector *cardProtector
}

// GeneratedCard is the one intentional plaintext-bearing response. Ordinary
// card models and list/search results contain metadata only.
type GeneratedCard struct {
	Card      model.CardSummary `json:"card"`
	Plaintext string            `json:"plaintext"`
}

func New(store Store, clock Clock, random io.Reader, encryptionKey, lookupKey []byte) (*Service, error) {
	if isNil(store) || isNil(clock) || random == nil {
		return nil, ErrInvalidRequest
	}
	protector, err := newCardProtector(encryptionKey, lookupKey, random)
	if err != nil {
		return nil, ErrInvalidRequest
	}
	return &Service{store: store, clock: clock, protector: protector}, nil
}

func (s *Service) GenerateCards(ctx context.Context, packageID string, durationDays, count int, defaultNote *string, perCardNotes []*string) ([]GeneratedCard, error) {
	var valid bool
	if count <= 0 || count > MaxGenerateCount {
		return nil, ErrInvalidRequest
	}
	packageID, valid = boundedIdentifier(packageID)
	if !valid || !validUUID(packageID) || !allowedDuration(durationDays) || (perCardNotes != nil && len(perCardNotes) != count) || !validNote(defaultNote) {
		return nil, ErrInvalidRequest
	}
	for _, note := range perCardNotes {
		if !validNote(note) {
			return nil, ErrInvalidRequest
		}
	}
	createdAt := s.clock.Now().UTC()
	if createdAt.IsZero() {
		return nil, ErrInvalidRequest
	}
	sealed := make([]model.SealedCard, count)
	plaintext := make([]string, count)
	for i := 0; i < count; i++ {
		cardText, err := s.protector.generate()
		if err != nil {
			clearStrings(plaintext)
			return nil, errors.New("generate license card")
		}
		cardID := uuid.NewString()
		protected, err := s.protector.seal(cardID, cardText)
		if err != nil {
			clearStrings(plaintext)
			return nil, errors.New("protect license card")
		}
		_, compact, _ := normalizeCard(cardText)
		sealed[i] = model.SealedCard{
			ID:         cardID,
			Ciphertext: protected.ciphertext,
			FullDigest: protected.digest,
			Prefix8:    compact[:8],
			Last4:      compact[len(compact)-4:],
		}
		if perCardNotes != nil && perCardNotes[i] != nil {
			sealed[i].Note = cloneString(perCardNotes[i])
		}
		plaintext[i] = cardText
	}
	request := model.GenerateCardsRequest{PackageID: packageID, Count: count, BaseDays: durationDays, Note: cloneString(defaultNote), CreatedAt: createdAt}
	var cards []model.Card
	var err error
	if tx, ok := adminMutationTransaction(ctx); ok {
		cards, err = tx.GenerateCards(ctx, request, sealed)
	} else {
		cards, err = s.store.GenerateCards(ctx, request, sealed)
	}
	if err != nil {
		clearStrings(plaintext)
		return nil, classifyStoreError(err)
	}
	if len(cards) != count {
		clearStrings(plaintext)
		return nil, errors.New("license store returned an invalid generated-card count")
	}
	result := make([]GeneratedCard, count)
	for i := range cards {
		result[i] = GeneratedCard{Card: summarize(cards[i]), Plaintext: plaintext[i]}
	}
	return result, nil
}

func (s *Service) Activate(ctx context.Context, requestID, cardText, softwareID, machineHash string) (model.ActivationState, error) {
	var valid bool
	if !validRawCardInput(cardText) {
		return model.ActivationState{}, ErrInvalidRequest
	}
	if requestID, valid = boundedIdentifier(requestID); !valid {
		return model.ActivationState{}, ErrInvalidRequest
	}
	if softwareID, valid = boundedIdentifier(softwareID); !valid {
		return model.ActivationState{}, ErrInvalidRequest
	}
	if machineHash, valid = boundedIdentifier(machineHash); !valid {
		return model.ActivationState{}, ErrInvalidRequest
	}
	card, now, err := s.authorizeCard(ctx, cardText, softwareID, machineHash, true)
	if err != nil {
		return model.ActivationState{}, err
	}
	packageName, err := s.store.GetPackageName(ctx, card.PackageID)
	if err != nil {
		return model.ActivationState{}, classifyStoreError(err)
	}
	state, err := s.store.Activate(ctx, model.ActivationMutation{
		RequestID: requestID, CardID: card.ID, SoftwareID: softwareID, MachineHash: machineHash,
		ObservedAt: now, LeaseUntil: now.Add(validationLease),
	})
	if err != nil {
		return model.ActivationState{}, classifyStoreError(err)
	}
	state.PackageName = packageName
	return state, nil
}

func (s *Service) Validate(ctx context.Context, cardText, softwareID, machineHash string) (model.ActivationState, error) {
	var valid bool
	if !validRawCardInput(cardText) {
		return model.ActivationState{}, ErrInvalidRequest
	}
	if softwareID, valid = boundedIdentifier(softwareID); !valid {
		return model.ActivationState{}, ErrInvalidRequest
	}
	if machineHash, valid = boundedIdentifier(machineHash); !valid {
		return model.ActivationState{}, ErrInvalidRequest
	}
	card, now, err := s.authorizeCard(ctx, cardText, softwareID, machineHash, false)
	if err != nil {
		return model.ActivationState{}, err
	}
	packageName, err := s.store.GetPackageName(ctx, card.PackageID)
	if err != nil {
		return model.ActivationState{}, classifyStoreError(err)
	}
	if card.ActivatedAt == nil || card.ExpiresAt == nil || card.MachineHash == nil {
		return model.ActivationState{}, ErrNotFound
	}
	leaseUntil := now.Add(validationLease)
	if leaseUntil.After(*card.ExpiresAt) {
		leaseUntil = *card.ExpiresAt
	}
	state, err := s.store.ValidateAndRecordLease(ctx, model.Lease{CardID: card.ID, SoftwareID: softwareID, MachineHash: machineHash, LastSeenAt: now, LeaseUntil: leaseUntil})
	if err != nil {
		return model.ActivationState{}, classifyStoreError(err)
	}
	state.PackageName = packageName
	return state, nil
}

func (s *Service) RenewCard(ctx context.Context, cardID string, days int) error {
	if !validUUID(cardID) || !allowedDuration(days) {
		return ErrInvalidRequest
	}
	now := s.clock.Now().UTC()
	if now.IsZero() {
		return ErrInvalidRequest
	}
	if tx, ok := adminMutationTransaction(ctx); ok {
		return classifyStoreError(tx.RenewCard(ctx, cardID, days, now))
	}
	return classifyStoreError(s.store.RenewCard(ctx, cardID, days, now))
}

func (s *Service) UnbindCard(ctx context.Context, cardID string) error {
	if !validUUID(cardID) {
		return ErrInvalidRequest
	}
	if tx, ok := adminMutationTransaction(ctx); ok {
		return classifyStoreError(tx.UnbindCard(ctx, cardID))
	}
	return classifyStoreError(s.store.UnbindCard(ctx, cardID))
}

func (s *Service) DeleteCard(ctx context.Context, cardID string) error {
	if !validUUID(cardID) {
		return ErrInvalidRequest
	}
	if tx, ok := adminMutationTransaction(ctx); ok {
		return classifyStoreError(tx.DeleteCard(ctx, cardID))
	}
	return classifyStoreError(s.store.DeleteCard(ctx, cardID))
}

func (s *Service) ListSoftware(ctx context.Context, page, pageSize int) ([]model.Software, int, error) {
	if page < 1 || pageSize < 1 {
		return nil, 0, ErrInvalidRequest
	}
	admin, ok := s.store.(AdminStore)
	if !ok {
		return nil, 0, errors.New("admin store unavailable")
	}
	items, total, err := admin.ListSoftware(ctx, (page-1)*pageSize, pageSize)
	return items, total, classifyStoreError(err)
}

func (s *Service) CreateSoftware(ctx context.Context, name string) (model.Software, error) {
	if model.ValidateAdminName(name) != nil {
		return model.Software{}, ErrInvalidRequest
	}
	now := s.clock.Now().UTC()
	if now.IsZero() {
		return model.Software{}, ErrInvalidRequest
	}
	var item model.Software
	var err error
	if tx, ok := adminMutationTransaction(ctx); ok {
		item, err = tx.CreateSoftwareAt(ctx, name, now)
	} else if admin, ok := s.store.(AdminStore); ok {
		item, err = admin.CreateSoftwareAt(ctx, name, now)
	} else {
		return model.Software{}, errors.New("admin store unavailable")
	}
	return item, classifyStoreError(err)
}

func (s *Service) RenameSoftware(ctx context.Context, id, name string) error {
	if !validUUID(id) || model.ValidateAdminName(name) != nil {
		return ErrInvalidRequest
	}
	if tx, ok := adminMutationTransaction(ctx); ok {
		return classifyStoreError(tx.RenameSoftware(ctx, id, name))
	}
	admin, ok := s.store.(AdminStore)
	if !ok {
		return errors.New("admin store unavailable")
	}
	return classifyStoreError(admin.RenameSoftware(ctx, id, name))
}

func (s *Service) SetSoftwareEnabled(ctx context.Context, id string, enabled bool) error {
	if !validUUID(id) {
		return ErrInvalidRequest
	}
	if tx, ok := adminMutationTransaction(ctx); ok {
		return classifyStoreError(tx.SetSoftwareEnabled(ctx, id, enabled))
	}
	admin, ok := s.store.(AdminStore)
	if !ok {
		return errors.New("admin store unavailable")
	}
	return classifyStoreError(admin.SetSoftwareEnabled(ctx, id, enabled))
}

func (s *Service) authorizeCard(ctx context.Context, cardText, softwareID, machineHash string, allowUnactivated bool) (model.Card, time.Time, error) {
	if !validRawCardInput(cardText) {
		return model.Card{}, time.Time{}, ErrInvalidRequest
	}
	_, compact, err := normalizeCard(cardText)
	if err != nil {
		return model.Card{}, time.Time{}, ErrInvalidRequest
	}
	digest := s.protector.digestCompact(compact)
	card, err := s.store.FindCardByDigest(ctx, digest)
	if err != nil {
		return model.Card{}, time.Time{}, classifyStoreError(err)
	}
	if !hmac.Equal([]byte(card.FullDigest), []byte(digest)) {
		return model.Card{}, time.Time{}, ErrNotFound
	}
	software, member, err := s.store.GetSoftwareAuthorization(ctx, card.PackageID, softwareID)
	if err != nil {
		if errors.Is(err, licensestore.ErrNotFound) {
			return model.Card{}, time.Time{}, ErrAppNotAllowed
		}
		return model.Card{}, time.Time{}, err
	}
	if !member {
		return model.Card{}, time.Time{}, ErrAppNotAllowed
	}
	if !software.Enabled {
		return model.Card{}, time.Time{}, ErrAppDisabled
	}
	now := s.clock.Now().UTC()
	if now.IsZero() {
		return model.Card{}, time.Time{}, ErrInvalidRequest
	}
	if card.MachineHash != nil && *card.MachineHash != machineHash {
		return model.Card{}, time.Time{}, ErrMachineMismatch
	}
	if card.ExpiresAt != nil && !card.ExpiresAt.After(now) {
		return model.Card{}, time.Time{}, ErrExpired
	}
	if !allowUnactivated && card.ActivatedAt == nil {
		return model.Card{}, time.Time{}, ErrNotFound
	}
	return card, now, nil
}

func allowedDuration(days int) bool {
	switch days {
	case 30, 90, 180, 365:
		return true
	default:
		return false
	}
}

func classifyStoreError(err error) error {
	if err == nil {
		return nil
	}
	switch {
	case errors.Is(err, licensestore.ErrNotFound):
		return ErrNotFound
	case errors.Is(err, licensestore.ErrMachineBound):
		return ErrMachineMismatch
	case errors.Is(err, licensestore.ErrLeaseNotBound):
		return ErrNotFound
	case errors.Is(err, licensestore.ErrSoftwareNotAllowed):
		return ErrAppNotAllowed
	case errors.Is(err, licensestore.ErrSoftwareDisabled):
		return ErrAppDisabled
	case errors.Is(err, licensestore.ErrLicenseExpired):
		return ErrExpired
	case errors.Is(err, licensestore.ErrConflict):
		return ErrConflict
	default:
		return err
	}
}

func boundedIdentifier(value string) (string, bool) {
	if value == "" || len(value) > model.MaxIdentifierLength {
		return "", false
	}
	value = strings.TrimSpace(value)
	return value, value != ""
}

func validRawCardInput(value string) bool {
	return value != "" && len(value) <= MaxRawCardInputBytes
}

func validNote(value *string) bool {
	return value == nil || (len(*value) <= MaxNoteUTF8Bytes && utf8.ValidString(*value))
}

func validUUID(value string) bool {
	parsed, err := uuid.Parse(value)
	return err == nil && parsed.String() == value
}
func validUUIDIDs(values []string, maximum int) bool {
	if len(values) < 1 || len(values) > maximum {
		return false
	}
	seen := make(map[string]struct{}, len(values))
	for _, value := range values {
		if !validUUID(value) {
			return false
		}
		if _, ok := seen[value]; ok {
			return false
		}
		seen[value] = struct{}{}
	}
	return true
}

func summarize(card model.Card) model.CardSummary {
	return model.CardSummary{ID: card.ID, PackageID: card.PackageID, Prefix8: card.Prefix8, Last4: card.Last4, BaseDays: card.BaseDays, UnactivatedTotalDays: card.UnactivatedTotalDays, Note: cloneString(card.Note), ActivatedAt: card.ActivatedAt, ExpiresAt: card.ExpiresAt, MachineHash: cloneString(card.MachineHash), CreatedAt: card.CreatedAt}
}

func cloneString(value *string) *string {
	if value == nil {
		return nil
	}
	copy := *value
	return &copy
}

func clearStrings(values []string) {
	for i := range values {
		values[i] = ""
	}
}

func isNil(value any) bool {
	if value == nil {
		return true
	}
	reflected := reflect.ValueOf(value)
	switch reflected.Kind() {
	case reflect.Chan, reflect.Func, reflect.Interface, reflect.Map, reflect.Pointer, reflect.Slice:
		return reflected.IsNil()
	default:
		return false
	}
}
