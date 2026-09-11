"""Deny-by-default dispatcher for fixed managed-session operations."""

from __future__ import annotations

import protocol


MUTATING_OPERATIONS = frozenset(("EnsureDirectory", "SubmitTransfer", "SubmitShare"))


class SessionOperationDispatcher:
    """Dispatches validated named operations without exposing a generic request surface."""

    def __init__(self, *, enabled_mutations=(), executor=None):
        enabled = frozenset(enabled_mutations)
        if not enabled.issubset(MUTATING_OPERATIONS):
            raise ValueError("only named mutations can be enabled")
        if executor is None or not callable(executor):
            raise ValueError("a fixed operation executor is required")
        self._enabled_mutations = enabled
        self._executor = executor

    def dispatch(self, value):
        request = protocol.validate_session_operation_request(value)
        operation = request["operation"]
        if operation in MUTATING_OPERATIONS and operation not in self._enabled_mutations:
            return {"kind": "ContractUnavailable"}
        return self._executor(request)
