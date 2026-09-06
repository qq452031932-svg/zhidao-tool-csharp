package deploy

import (
	"context"
	"errors"
	"os"
	"path/filepath"

	"fanfh.cn/superpan/ota-admin/internal/release"
	"fanfh.cn/superpan/ota-admin/internal/store"
)

func (disk *Disk) RollbackGenerationAuthorized(ctx context.Context, operationID, name, expectedVersion string, expectedAssets []store.ReleaseAsset, validator Validator, authorize func(context.Context) error) (Generation, bool, error) {
	if disk == nil || validator == nil || authorize == nil || expectedVersion == "" || len(expectedAssets) == 0 || !canonicalReleaseID.MatchString(operationID) {
		return Generation{}, false, errors.New("rollback request is invalid")
	}
	path, err := disk.GenerationPath(name)
	if err != nil {
		return Generation{}, false, err
	}
	envelope, rootDescriptor, closeEnvelope, err := disk.openGenerationEnvelope(name)
	if err != nil {
		return Generation{}, false, err
	}
	report, validateErr := validateOpenedGeneration(ctx, validator, envelope, rootDescriptor)
	if identityErr := disk.verifyOpenedGeneration(name, rootDescriptor); identityErr != nil {
		validateErr = errors.Join(validateErr, identityErr)
	}
	assets := reportAssets(report)
	if validateErr != nil || !report.Verified || report.Version != expectedVersion || !sameAssets(assets, expectedAssets) {
		closeErr := closeEnvelope()
		if validateErr != nil {
			return Generation{}, false, errors.Join(validateErr, closeErr)
		}
		return Generation{}, false, errors.Join(errors.New("rollback target does not match its committed release"), closeErr)
	}
	switched, switchErr := disk.switchStableForRollback(ctx, operationID, name, rootDescriptor, authorize)
	closeErr := closeEnvelope()
	return Generation{Name: name, Path: path, Report: report, Assets: assets}, switched, errors.Join(switchErr, closeErr)
}

func reportAssets(report release.Report) []store.ReleaseAsset {
	assets := make([]store.ReleaseAsset, 0, len(report.Files))
	for _, file := range report.Files {
		assets = append(assets, store.ReleaseAsset{FileName: file.Name, SizeBytes: file.Size, SHA256: file.SHA256})
	}
	return assets
}

func (disk *Disk) switchStableForRollback(ctx context.Context, operationID, name string, validatedRoot *os.File, authorize func(context.Context) error) (bool, error) {
	if err := disk.bound.verify(); err != nil {
		return false, err
	}
	if err := disk.verifyOpenedGeneration(name, validatedRoot); err != nil {
		return false, err
	}
	target := filepath.ToSlash(filepath.Join("generations", name))
	tempLinkName := ".stable-rollback-" + operationID + ".tmp"
	if _, err := disk.bound.readRootLink(tempLinkName); err == nil {
		return false, errors.New("temporary rollback link already exists")
	} else if !errors.Is(err, os.ErrNotExist) {
		return false, err
	}
	if err := disk.bound.symlinkRoot(target, tempLinkName); err != nil {
		return false, err
	}
	cleanup := func(cause error) error {
		removeErr := disk.bound.unlinkRoot(tempLinkName)
		if errors.Is(removeErr, os.ErrNotExist) {
			removeErr = nil
		}
		return errors.Join(cause, removeErr, disk.bound.syncRoot(), disk.bound.verify())
	}
	if err := disk.bound.verify(); err != nil {
		return false, cleanup(err)
	}
	if err := disk.verifyOpenedGeneration(name, validatedRoot); err != nil {
		return false, cleanup(err)
	}
	if err := authorize(ctx); err != nil {
		return false, cleanup(err)
	}
	if err := disk.bound.renameRoot(tempLinkName, "stable"); err != nil {
		return false, cleanup(err)
	}
	if err := disk.boundary(BoundaryRollbackStable); err != nil {
		return true, err
	}
	if err := disk.bound.syncRoot(); err != nil {
		return true, err
	}
	if err := disk.boundary(BoundaryRollbackParentSynced); err != nil {
		return true, err
	}
	if err := disk.bound.verify(); err != nil {
		return true, err
	}
	if err := disk.verifyOpenedGeneration(name, validatedRoot); err != nil {
		return true, err
	}
	return true, nil
}

func (disk *Disk) abortRollback(operationID, generationName string) error {
	if disk == nil || !canonicalReleaseID.MatchString(operationID) {
		return errors.New("rollback operation ID is invalid")
	}
	tempLinkName := ".stable-rollback-" + operationID + ".tmp"
	target, err := disk.bound.readRootLink(tempLinkName)
	if errors.Is(err, os.ErrNotExist) {
		return nil
	}
	if err != nil {
		return err
	}
	if filepath.ToSlash(target) != filepath.ToSlash(filepath.Join("generations", generationName)) {
		return errors.New("temporary rollback target is unsafe")
	}
	if err := disk.bound.unlinkRoot(tempLinkName); err != nil {
		return err
	}
	return errors.Join(disk.bound.syncRoot(), disk.bound.verify())
}

func (disk *Disk) RemoveRetainedGeneration(name string) error {
	if _, err := disk.GenerationPath(name); err != nil {
		return err
	}
	if err := disk.bound.verify(); err != nil {
		return err
	}
	current, err := disk.StableTarget()
	if err != nil {
		return err
	}
	if current == name {
		return errors.New("current generation cannot be removed")
	}
	if err := disk.remove(disk.generations, name); err != nil {
		return err
	}
	return disk.bound.verify()
}
