import copy
import unittest
import uuid

import protocol
import transfer_source


def payload():
    return {
        "browserId": str(uuid.uuid4()),
        "targetId": "target-1",
        "devToolsEndpoint": "http://127.0.0.1:43127/",
        "timeoutMilliseconds": 30000,
        "sourceUri": "https://pan.baidu.com/s/share-id?pwd=1111",
        "expectedBaiduUserId": "uid-1",
        "fixedCode": "1111",
    }


class Target:
    target_id = "target-1"

    def __init__(self):
        self.calls = []

    def current_url(self, _remaining):
        return "https://pan.baidu.com/s/share-id?pwd=1111"

    def submit_extraction_code(self, code, _remaining):
        self.calls.append(code)
        return {"kind": "ClickAttempted", "dispatchState": "ClickDispatched"}

    def observe_source_stage(self, _remaining):
        return {"kind": "SourceOpened"}


class Connector:
    def __init__(self, browser_id, target):
        self.browser_id = browser_id
        self.target = target

    def get_target(self, target_id, _remaining):
        return self.target if target_id == self.target.target_id else None


class TransferExtractionTests(unittest.TestCase):
    def test_protocol_and_bound_action_allow_only_fixed_four_character_code(self):
        body = payload()
        request = {
            "protocolVersion": protocol.PROTOCOL_VERSION,
            "executionEngineVersion": protocol.EXECUTION_ENGINE_VERSION,
            "correlationId": str(uuid.uuid4()),
            "operationId": str(uuid.uuid4()),
            "accountId": str(uuid.uuid4()),
            "generation": 1,
            "action": "SubmitExtractionCode",
            "payload": body,
        }
        protocol.validate_request(request)
        target = Target()

        result = transfer_source.submit_extraction_bound(
            body, Connector(body["browserId"], target),
            classify_route=lambda value: "ExternalShare")

        self.assertEqual(
            {"kind": "SourceOpened", "dispatchState": "ClickDispatched"}, result)
        self.assertEqual(["1111"], target.calls)

        for code in ("", "abc", "abcde", "**11"):
            invalid = copy.deepcopy(request)
            invalid["payload"]["fixedCode"] = code
            with self.assertRaises(protocol.ProtocolError):
                protocol.validate_request(invalid)

    def test_arbitrary_dom_and_replay_fields_are_rejected(self):
        body = payload()
        for field in ("selector", "script", "url", "retry"):
            changed = dict(body)
            changed[field] = "private"
            with self.assertRaises(protocol.ProtocolError):
                protocol.validate_submit_extraction_code_payload(changed)


if __name__ == "__main__":
    unittest.main()
