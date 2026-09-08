package service

import (
	"context"
	"crypto/hmac"
	"errors"
	"io"
	"reflect"
	"strings"
	"time"

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
	Activate(context.Context, model.ActivationMutation) (model.ActivationState, error)
	ValidateAndRecordLease(context.Context, model.Lease) (model.ActivationState, error)
	RenewCard(context.Context, string, int, time.Time) error
	UnbindCard(context.Context, string) error
	DeleteCard(context.Context, string) error
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
	if !valid || !allowedDuration(durationDays) || (perCardNotes != nil && len(perCardNotes) != count) || !validNote(defaultNote) {
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
	cards, err := s.store.GenerateCards(ctx, model.GenerateCardsRequest{PackageID: packageID, Count: count, BaseDays: durationDays, Note: cloneString(defaultNote), CreatedAt: createdAt}, sealed)
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
	state, err := s.store.Activate(ctx, model.ActivationMutation{
		RequestID: requestID, CardID: card.ID, SoftwareID: softwareID, MachineHash: machineHash,
		ObservedAt: now, LeaseUntil: now.Add(validationLease),
	})
	if err != nil {
		return model.ActivationState{}, classifyStoreError(err)
	}
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
	return state, nil
}

func (s *Service) RenewCard(ctx context.Context, cardID string, days int) error {
	var valid bool
	if cardID, valid = boundedIdentifier(cardID); !valid || !allowedDuration(days) {
		return ErrInvalidRequest
	}
	now := s.clock.Now().UTC()
	if now.IsZero() {
		return ErrInvalidRequest
	}
	return classifyStoreError(s.store.RenewCard(ctx, cardID, days, now))
}

func (s *Service) UnbindCard(ctx context.Context, cardID string) error {
	var valid bool
	if cardID, valid = boundedIdentifier(cardID); !valid {
		return ErrInvalidRequest
	}
	return classifyStoreError(s.store.UnbindCard(ctx, cardID))
}

func (s *Service) DeleteCard(ctx context.Context, cardID string) error {
	var valid bool
	if cardID, valid = boundedIdentifier(cardID); !valid {
		return ErrInvalidRequest
	}
	return classifyStoreError(s.store.DeleteCard(ctx, cardID))
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
	return value == nil || len(*value) <= MaxNoteUTF8Bytes
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
