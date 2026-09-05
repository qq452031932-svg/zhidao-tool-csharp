package upload

import "errors"

func classifyStorageError(err error) error {
	if err == nil || !isInsufficientDiskError(err) {
		return err
	}
	return errors.Join(ErrInsufficientDisk, err)
}
