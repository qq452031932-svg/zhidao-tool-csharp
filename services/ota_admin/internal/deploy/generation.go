package deploy

import (
	"context"
	"errors"
	"io"
	"math"
	"os"
	"path/filepath"
	"reflect"
	"sort"

	"fanfh.cn/superpan/ota-admin/internal/release"
	"fanfh.cn/superpan/ota-admin/internal/store"
	"fanfh.cn/superpan/ota-admin/internal/upload"
)

type Validator interface {
	Validate(context.Context, *upload.Envelope, string) (release.Report, error)
}

type openedDirectoryValidator interface {
	ValidateOpenedDirectory(context.Context, *upload.Envelope, *os.File, string) (release.Report, error)
}

type Generation struct {
	Name, Path string
	Report     release.Report
	Assets     []store.ReleaseAsset
}

func (disk *Disk) PublishGeneration(ctx context.Context, source *upload.Envelope, releaseID string, expected release.Report, validator Validator) (Generation, bool, error) {
	return disk.publishGeneration(ctx, source, releaseID, expected, validator, nil)
}

func (disk *Disk) PublishGenerationAuthorized(ctx context.Context, source *upload.Envelope, releaseID string, expected release.Report, validator Validator, authorize func(context.Context) error) (Generation, bool, error) {
	if authorize == nil {
		return Generation{}, false, errors.New("generation authorization is required")
	}
	return disk.publishGeneration(ctx, source, releaseID, expected, validator, authorize)
}

func (disk *Disk) publishGeneration(ctx context.Context, source *upload.Envelope, releaseID string, expected release.Report, validator Validator, authorize func(context.Context) error) (Generation, bool, error) {
	if validator == nil || !expected.Verified {
		return Generation{}, false, errors.New("generation validator is required")
	}
	name, cleanup, err := disk.stage(ctx, source, releaseID)
	if err != nil {
		return Generation{}, false, err
	}
	path, _ := disk.GenerationPath(name)
	stagedName := "." + name + ".tmp"
	envelope, rootDescriptor, closeEnvelope, err := disk.openGenerationEnvelope(stagedName)
	if err != nil {
		if cleanupErr := cleanup(); cleanupErr != nil {
			return Generation{}, false, errors.Join(err, ErrCleanupIncomplete, cleanupErr)
		}
		return Generation{}, false, err
	}
	report, err := validateOpenedGeneration(ctx, validator, envelope, rootDescriptor)
	if identityErr := disk.verifyOpenedGeneration(stagedName, rootDescriptor); identityErr != nil {
		err = errors.Join(err, identityErr)
	}
	if err != nil || !reflect.DeepEqual(report, expected) {
		closeErr := closeEnvelope()
		if closeErr != nil {
			err = errors.Join(err, closeErr)
		}
		cleanupErr := cleanup()
		if err != nil {
			if cleanupErr != nil {
				return Generation{}, false, errors.Join(err, ErrCleanupIncomplete, cleanupErr)
			}
			return Generation{}, false, err
		}
		mismatch := errors.New("generation validation report changed")
		if cleanupErr != nil {
			mismatch = errors.Join(mismatch, ErrCleanupIncomplete, cleanupErr)
		}
		return Generation{}, false, mismatch
	}
	assets := make([]store.ReleaseAsset, 0, len(report.Files))
	for _, file := range report.Files {
		assets = append(assets, store.ReleaseAsset{FileName: file.Name, SizeBytes: file.Size, SHA256: file.SHA256})
	}
	switched, err := disk.commitOpenedAuthorized(name, cleanup, func() error {
		if authorize == nil {
			return nil
		}
		return authorize(ctx)
	}, rootDescriptor)
	if closeErr := closeEnvelope(); closeErr != nil {
		err = errors.Join(err, closeErr)
	}
	return Generation{Name: name, Path: path, Report: report, Assets: assets}, switched, err
}

func (disk *Disk) RecoverGeneration(ctx context.Context, validator Validator) (Generation, error) {
	name, err := disk.StableTarget()
	if err != nil {
		return Generation{}, err
	}
	return disk.ValidateGeneration(ctx, name, validator)
}

// ValidateGeneration fully validates one named immutable generation while it
// remains bound to the opened directory descriptor. It is used at startup to
// reconcile retained release metadata with the same signed source of truth as
// the current stable generation.
func (disk *Disk) ValidateGeneration(ctx context.Context, name string, validator Validator) (Generation, error) {
	if disk == nil || validator == nil {
		return Generation{}, errors.New("generation validator is required")
	}
	path, err := disk.GenerationPath(name)
	if err != nil {
		return Generation{}, err
	}
	envelope, rootDescriptor, closeEnvelope, err := disk.openGenerationEnvelope(name)
	if err != nil {
		return Generation{}, err
	}
	report, err := validateOpenedGeneration(ctx, validator, envelope, rootDescriptor)
	if identityErr := disk.verifyOpenedGeneration(name, rootDescriptor); identityErr != nil {
		err = errors.Join(err, identityErr)
	}
	closeErr := closeEnvelope()
	if closeErr != nil {
		err = errors.Join(err, closeErr)
	}
	if err != nil {
		return Generation{}, err
	}
	assets := make([]store.ReleaseAsset, 0, len(report.Files))
	for _, file := range report.Files {
		assets = append(assets, store.ReleaseAsset{FileName: file.Name, SizeBytes: file.Size, SHA256: file.SHA256})
	}
	return Generation{Name: name, Path: path, Report: report, Assets: assets}, nil
}

func validateOpenedGeneration(ctx context.Context, validator Validator, envelope *upload.Envelope, rootDescriptor *os.File) (release.Report, error) {
	if rootDescriptor == nil {
		return validator.Validate(ctx, envelope, "")
	}
	descriptorValidator, ok := validator.(openedDirectoryValidator)
	if !ok {
		return release.Report{}, errors.New("Linux generation validator must accept an opened directory")
	}
	return descriptorValidator.ValidateOpenedDirectory(ctx, envelope, rootDescriptor, "")
}

func generationEnvelope(root string) (*upload.Envelope, error) {
	info, err := os.Lstat(root)
	if err != nil || validateImmutableGeneration(info, true) != nil {
		return nil, errors.New("generation root is unsafe")
	}
	handle, err := os.Open(root)
	if err != nil {
		return nil, err
	}
	entries, readErr := handle.ReadDir(maxGenerationEntries + 1)
	closeErr := handle.Close()
	if readErr != nil && !errors.Is(readErr, io.EOF) {
		return nil, readErr
	}
	if closeErr != nil {
		return nil, closeErr
	}
	if len(entries) == 0 || len(entries) > maxGenerationEntries {
		return nil, errors.New("generation closure is invalid")
	}
	sort.Slice(entries, func(i, j int) bool { return entries[i].Name() < entries[j].Name() })
	result := make([]upload.Entry, 0, len(entries))
	var expanded int64
	for _, entry := range entries {
		path := filepath.Join(root, entry.Name())
		child, err := os.Lstat(path)
		if err != nil || validateImmutableGeneration(child, false) != nil || child.Size() < 0 || child.Size() > math.MaxInt64-expanded {
			return nil, errors.New("generation member is unsafe")
		}
		expanded += child.Size()
		result = append(result, upload.Entry{Name: entry.Name(), Path: path, Size: child.Size()})
	}
	return &upload.Envelope{ExtractionRoot: root, ExpandedBytes: expanded, Entries: result}, nil
}
