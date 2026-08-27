DROP TRIGGER TR_TransferOperations_RejectCleanupClaim_Insert;
DROP TRIGGER TR_ShareOperations_RejectCleanupClaim_Insert;
DROP TRIGGER TR_StandaloneShareOperations_RejectCleanupClaim_Insert;
DROP TRIGGER TR_WebAutomationAttempts_RejectCleanupClaim_Insert;

CREATE TRIGGER TR_TransferOperations_RejectCleanupClaim_Insert
BEFORE INSERT ON TransferOperations
WHEN NEW.ExecutionEngineVersion='ManagedChromium-v1' AND (
  NOT EXISTS(
    SELECT 1 FROM ManagedBrowserSessions session
    WHERE session.SessionId=NEW.SessionId AND session.InstallationId=NEW.InstallationId
      AND session.AccountId=NEW.AssignedAccountId
      AND session.BrowserFamily=NEW.BrowserFamily AND session.ProfileId=NEW.ProfileId
      AND session.State='Active')
  OR EXISTS(
    SELECT 1 FROM ManagedBrowserCleanupClaims claim
    WHERE claim.InstallationId=NEW.InstallationId AND claim.SessionId=NEW.SessionId))
BEGIN
    SELECT RAISE(ABORT, 'managed browser identity is not active');
END;

CREATE TRIGGER TR_ShareOperations_RejectCleanupClaim_Insert
BEFORE INSERT ON ShareOperations
WHEN NEW.ExecutionEngineVersion='ManagedChromium-v1' AND (
  NOT EXISTS(
    SELECT 1 FROM ManagedBrowserSessions session
    WHERE session.SessionId=NEW.SessionId AND session.InstallationId=NEW.InstallationId
      AND session.AccountId=NEW.AccountId
      AND session.BrowserFamily=NEW.BrowserFamily AND session.ProfileId=NEW.ProfileId
      AND session.State='Active')
  OR EXISTS(
    SELECT 1 FROM ManagedBrowserCleanupClaims claim
    WHERE claim.InstallationId=NEW.InstallationId AND claim.SessionId=NEW.SessionId))
BEGIN
    SELECT RAISE(ABORT, 'managed browser identity is not active');
END;

CREATE TRIGGER TR_StandaloneShareOperations_RejectCleanupClaim_Insert
BEFORE INSERT ON StandaloneShareOperations
WHEN NEW.ExecutionEngineVersion='ManagedChromium-v1' AND (
  NOT EXISTS(
    SELECT 1 FROM ManagedBrowserSessions session
    WHERE session.SessionId=NEW.SessionId AND session.InstallationId=NEW.InstallationId
      AND session.AccountId=NEW.AccountId
      AND session.BrowserFamily=NEW.BrowserFamily AND session.ProfileId=NEW.ProfileId
      AND session.State='Active')
  OR EXISTS(
    SELECT 1 FROM ManagedBrowserCleanupClaims claim
    WHERE claim.InstallationId=NEW.InstallationId AND claim.SessionId=NEW.SessionId))
BEGIN
    SELECT RAISE(ABORT, 'managed browser identity is not active');
END;

CREATE TRIGGER TR_WebAutomationAttempts_RejectCleanupClaim_Insert
BEFORE INSERT ON WebAutomationAttempts
WHEN NEW.ExecutionEngineVersion='ManagedChromium-v1' AND (
  NOT EXISTS(
    SELECT 1 FROM ManagedBrowserSessions session
    WHERE session.SessionId=NEW.SessionId AND session.InstallationId=NEW.InstallationId
      AND session.AccountId=NEW.AccountId
      AND session.BrowserFamily=NEW.BrowserFamily AND session.ProfileId=NEW.ProfileId
      AND session.State='Active')
  OR EXISTS(
    SELECT 1 FROM ManagedBrowserCleanupClaims claim
    WHERE claim.InstallationId=NEW.InstallationId AND claim.SessionId=NEW.SessionId))
BEGIN
    SELECT RAISE(ABORT, 'managed browser identity is not active');
END;

DROP TRIGGER TR_ManagedBrowserCleanupClaims_Validate_Insert;

CREATE TRIGGER TR_ManagedBrowserCleanupClaims_Validate_Insert
BEFORE INSERT ON ManagedBrowserCleanupClaims
WHEN NOT EXISTS(
       SELECT 1 FROM BrowserInstallations installation
       WHERE installation.InstallationId=NEW.InstallationId
         AND installation.Revision=NEW.ExpectedInstallationRevision)
  OR NOT EXISTS(
       SELECT 1 FROM ManagedBrowserSessions session
       WHERE session.SessionId=NEW.SessionId AND session.InstallationId=NEW.InstallationId
         AND session.AccountId=NEW.AccountId AND session.State='Retired'
         AND session.Revision=NEW.ExpectedSessionRevision)
  OR NOT EXISTS(
       SELECT 1 FROM ManagedProfileCleanupManifests manifest
       WHERE manifest.ManifestId=NEW.ManifestId AND manifest.SessionId=NEW.SessionId
         AND manifest.AccountId=NEW.AccountId AND manifest.State='Prepared'
         AND manifest.Revision=NEW.ExpectedManifestRevision
         AND manifest.ConfirmationTokenHash IS NULL)
  OR EXISTS(
       SELECT 1 FROM TransferOperations operation
       WHERE operation.InstallationId=NEW.InstallationId AND operation.SessionId=NEW.SessionId
         AND operation.Status NOT IN ('Succeeded','Failed','Cancelled'))
  OR EXISTS(
       SELECT 1 FROM ShareOperations operation
       WHERE operation.InstallationId=NEW.InstallationId AND operation.SessionId=NEW.SessionId
         AND operation.Status NOT IN ('Succeeded','Failed','ReusedExisting','Cancelled'))
  OR EXISTS(
       SELECT 1 FROM StandaloneShareOperations operation
       WHERE operation.InstallationId=NEW.InstallationId AND operation.SessionId=NEW.SessionId
         AND operation.Status NOT IN ('Succeeded','Failed','Cancelled'))
  OR EXISTS(
       SELECT 1 FROM WebAutomationAttempts attempt
       WHERE attempt.InstallationId=NEW.InstallationId AND attempt.SessionId=NEW.SessionId
         AND attempt.State NOT IN ('Succeeded','DefinitelyNotSubmitted'))
  OR EXISTS(
       SELECT 1 FROM ManagedProfileCleanupManifests manifest
       WHERE manifest.SessionId=NEW.SessionId AND manifest.State<>'Deleted'
         AND manifest.ManifestId<>NEW.ManifestId)
BEGIN
    SELECT RAISE(ABORT, 'managed browser cleanup claim is not eligible');
END;
