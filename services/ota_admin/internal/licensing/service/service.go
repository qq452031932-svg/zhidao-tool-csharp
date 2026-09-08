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

type Clock interface {
	Now() time.Time
}

type Store interface {
	GenerateCards(context.Context, model.GenerateCardsRequest, []model.SealedCard) ([]model.Card, error)
	FindCardByDigest(context.Context, string) (model.Card, error)
	GetSoftwareAuthorization(context.Context, string, string) (model.Software, bool, error)
	Activate(context.Context, model.ActivationMutation) (model.ActivationState, error)
	RecordLease(context.Context, model.Lease) (model.Lease, error)
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
	if strings.TrimSpace(packageID) == "" || !allowedDuration(durationDays) || count <= 0 || (perCardNotes != nil && len(perCardNotes) != count) {
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
		protected, err := s.protector.seal(cardText)
		if err != nil {
			clearStrings(plaintext)
			return nil, errors.New("protect license card")
		}
		_, compact, _ := normalizeCard(cardText)
		sealed[i] = model.SealedCard{
			ID:         uuid.NewString(),
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
	cards, err := s.store.GenerateCards(ctx, model.GenerateCardsRequest{PackageID: strings.TrimSpace(packageID), Count: count, BaseDays: durationDays, Note: cloneString(defaultNote)}, sealed)
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
	if strings.TrimSpace(requestID) == "" || strings.TrimSpace(softwareID) == "" || strings.TrimSpace(machineHash) == "" {
		return model.ActivationState{}, ErrInvalidRequest
	}
	card, now, err := s.authorizeCard(ctx, cardText, softwareID, machineHash, true)
	if err != nil {
		return model.ActivationState{}, err
	}
	activatedAt := now
	var expiresAt time.Time
	if card.ActivatedAt != nil && card.ExpiresAt != nil {
		activatedAt = *card.ActivatedAt
		expiresAt = *card.ExpiresAt
	} else {
		expiresAt, err = addDays(now, card.UnactivatedTotalDays)
		if err != nil {
			return model.ActivationState{}, ErrInvalidRequest
		}
	}
	state, err := s.store.Activate(ctx, model.ActivationMutation{
		RequestID: strings.TrimSpace(requestID), CardID: card.ID, SoftwareID: strings.TrimSpace(softwareID), MachineHash: strings.TrimSpace(machineHash),
		ActivatedAt: activatedAt, ExpiresAt: expiresAt, LeaseUntil: now.Add(validationLease),
	})
	if err != nil {
		return model.ActivationState{}, classifyStoreError(err)
	}
	return state, nil
}

func (s *Service) Validate(ctx context.Context, cardText, softwareID, machineHash string) (model.ActivationState, error) {
	if strings.TrimSpace(softwareID) == "" || strings.TrimSpace(machineHash) == "" {
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
	lease, err := s.store.RecordLease(ctx, model.Lease{CardID: card.ID, SoftwareID: strings.TrimSpace(softwareID), MachineHash: strings.TrimSpace(machineHash), LastSeenAt: now, LeaseUntil: leaseUntil})
	if err != nil {
		if errors.Is(err, licensestore.ErrLeaseNotBound) {
			return model.ActivationState{}, ErrNotFound
		}
		return model.ActivationState{}, classifyStoreError(err)
	}
	return model.ActivationState{CardID: card.ID, SoftwareID: strings.TrimSpace(softwareID), MachineHash: strings.TrimSpace(machineHash), ActivatedAt: *card.ActivatedAt, ExpiresAt: *card.ExpiresAt, LeaseUntil: lease.LeaseUntil}, nil
}

func (s *Service) RenewCard(ctx context.Context, cardID string, days int) error {
	if strings.TrimSpace(cardID) == "" || !allowedDuration(days) {
		return ErrInvalidRequest
	}
	now := s.clock.Now().UTC()
	if now.IsZero() {
		return ErrInvalidRequest
	}
	return classifyStoreError(s.store.RenewCard(ctx, strings.TrimSpace(cardID), days, now))
}

func (s *Service) UnbindCard(ctx context.Context, cardID string) error {
	if strings.TrimSpace(cardID) == "" {
		return ErrInvalidRequest
	}
	return classifyStoreError(s.store.UnbindCard(ctx, strings.TrimSpace(cardID)))
}

func (s *Service) DeleteCard(ctx context.Context, cardID string) error {
	if strings.TrimSpace(cardID) == "" {
		return ErrInvalidRequest
	}
	return classifyStoreError(s.store.DeleteCard(ctx, strings.TrimSpace(cardID)))
}

func (s *Service) authorizeCard(ctx context.Context, cardText, softwareID, machineHash string, allowUnactivated bool) (model.Card, time.Time, error) {
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
	software, member, err := s.store.GetSoftwareAuthorization(ctx, card.PackageID, strings.TrimSpace(softwareID))
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
	if card.MachineHash != nil && *card.MachineHash != strings.TrimSpace(machineHash) {
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

func addDays(base time.Time, days int) (time.Time, error) {
	if days <= 0 {
		return time.Time{}, ErrInvalidRequest
	}
	const secondsPerDay = int64(24 * 60 * 60)
	delta := int64(days) * secondsPerDay
	if delta/secondsPerDay != int64(days) || base.Unix() > int64(^uint64(0)>>1)-delta {
		return time.Time{}, ErrInvalidRequest
	}
	return time.Unix(base.Unix()+delta, 0).UTC(), nil
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
	default:
		return err
	}
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
