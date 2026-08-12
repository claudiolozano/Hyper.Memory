import importlib.util
import json
import sys
import tempfile
import types
import unittest
from pathlib import Path


class _MemoryProvider:
    pass


agent_module = types.ModuleType("agent")
memory_provider_module = types.ModuleType("agent.memory_provider")
memory_provider_module.MemoryProvider = _MemoryProvider
sys.modules.setdefault("agent", agent_module)
sys.modules["agent.memory_provider"] = memory_provider_module

PLUGIN = Path(__file__).parents[1] / "src" / "HyperMemory.Bridge" / "hermes-plugin" / "__init__.py"
spec = importlib.util.spec_from_file_location("hypermemory_plugin_under_test", PLUGIN)
module = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(module)


class HyperMemoryProviderTests(unittest.TestCase):
    @staticmethod
    def _hit(version, user, assistant, score):
        return {
            "score": score,
            "atom": {
                "versionId": version,
                "occurredAt": "2026-08-12T10:00:00Z",
                "content": f"User request:\n{user}\n\nHermes response:\n{assistant}",
            },
            "citation": {},
            "evidence": {},
        }

    def test_request_audit_filters_prior_topic_and_ignores_assistant_claims(self):
        provider = module.HyperMemoryProvider()
        with tempfile.TemporaryDirectory() as home:
            provider.initialize("session", hermes_home=home)
            provider._request_json = lambda *_: [
                self._hit("game", "hicimos un juego en HTML", "Motor AAA completo", 0.95),
                self._hit(
                    "flight",
                    "dime el valor de los vuelos a Argentina más baratos y dame 3 posibilidades",
                    "Respuesta equivocada sobre el juego",
                    0.70,
                ),
            ]
            recalled = provider.prefetch("¿con respecto a vuelos hoy te pregunté algo?")
        self.assertIn("vuelos a Argentina", recalled)
        self.assertNotIn("hicimos un juego", recalled)
        self.assertNotIn("Respuesta equivocada", recalled)
        self.assertIn("AUDIT MODE", recalled)

    def test_history_output_separates_primary_request_from_unverified_response(self):
        provider = module.HyperMemoryProvider()
        with tempfile.TemporaryDirectory() as home:
            provider.initialize("session", hermes_home=home)
            provider._request_json = lambda *_: [
                self._hit("story", "escribe un cuento de 20 párrafos", "La Arquitectura del Instante", 0.8)
            ]
            recalled = provider.prefetch("¿hicimos un cuento de 20 párrafos?")
        self.assertIn("ORIGINAL_USER_REQUEST", recalled)
        self.assertIn("PAST_ASSISTANT_OUTPUT_UNVERIFIED", recalled)
        self.assertIn("does not prove completion", recalled)

    def test_recall_surfaces_verified_files_and_graph_reasons_without_claiming_execution(self):
        provider = module.HyperMemoryProvider()
        with tempfile.TemporaryDirectory() as home:
            provider.initialize("session", hermes_home=home)
            hit = self._hit("game", "crea un juego", "El juego funciona perfectamente", 0.9)
            hit["atom"]["metadata"] = {
                "artifacts.verifiedFiles": json.dumps([{
                    "path": "src/game.js",
                    "sha256": "A" * 64,
                }]),
                "execution.events": json.dumps([{
                    "command": "python -m pytest tests/test_game.py",
                    "exitCode": 0,
                    "status": "succeeded",
                    "workdir": ".",
                    "verification": {
                        "status": "passed", "kind": "test", "scope": "targeted",
                        "canonicalCommand": "pytest",
                    },
                }]),
            }
            hit["knowledge"] = {"score": 0.72, "reasons": ["file:src/game.js"]}
            provider._request_json = lambda *_: [hit]
            recalled = provider.prefetch("¿funcionaba el juego?")
        self.assertIn("VERIFIED_WORKSPACE_FILES", recalled)
        self.assertIn("src/game.js SHA256:", recalled)
        self.assertIn("KNOWLEDGE_LINKS", recalled)
        self.assertIn("file:src/game.js", recalled)
        self.assertIn("EXECUTION_EVIDENCE", recalled)
        self.assertIn("CHECK PASSED kind=test scope=targeted", recalled)
        self.assertIn("MANDATORY SCOPE LIMIT", recalled)
        self.assertIn("does not by itself prove that the program executed", provider.system_prompt_block())
        self.assertIn("targeted checks never prove the whole project", provider.system_prompt_block())

    def test_completion_question_without_external_evidence_forces_abstention(self):
        provider = module.HyperMemoryProvider()
        with tempfile.TemporaryDirectory() as home:
            provider.initialize("session", hermes_home=home)
            provider._request_json = lambda *_: [
                self._hit("game", "crea un juego", "Está terminado y funciona", 0.9)
            ]
            recalled = provider.prefetch("¿el juego funciona correctamente?")
        self.assertIn("MANDATORY ABSTENTION", recalled)
        self.assertIn("no independent completion evidence", recalled)
        self.assertIn("PAST_ASSISTANT_OUTPUT_UNVERIFIED", recalled)

    def test_catalog_query_expands_to_underlying_creation_turns(self):
        provider = module.HyperMemoryProvider()
        with tempfile.TemporaryDirectory() as home:
            provider.initialize("session", hermes_home=home)

            def request(_, payload):
                if "escribeme" in payload["text"]:
                    return [
                        self._hit("ten", "escríbeme un cuento de 10 párrafos", "**El Coleccionista de Segundos Perdidos**\n\n" + "Texto. " * 200, 0.8),
                        self._hit("twenty", "crea un cuento de 20 párrafos", "**La Arquitectura del Instante Suspendido**\n\n" + "Texto. " * 200, 0.8),
                        self._hit("cortazar", "escribimos algo que tenga que ver con Cortázar", "### El Inventario de las Horas Muertas\n\n" + "Texto. " * 200, 0.8),
                    ]
                return [self._hit("meta", "dame los títulos de los cuentos", "No hubo cuentos", 0.95)]

            provider._request_json = request
            recalled = provider.prefetch("Enumera todos los títulos de cuentos que generaste hoy")
        self.assertNotIn("AUDIT MODE", recalled)
        self.assertIn("El Coleccionista de Segundos Perdidos", recalled)
        self.assertIn("La Arquitectura del Instante Suspendido", recalled)
        self.assertIn("El Inventario de las Horas Muertas", recalled)
        self.assertIn("AUTHORITATIVE DISTINCT TITLE CATALOG", recalled)
        self.assertNotIn("No hubo cuentos", recalled)

    def test_installer_connection_file_casing_is_supported(self):
        provider = module.HyperMemoryProvider()
        with tempfile.TemporaryDirectory() as home:
            connection = Path(home) / "plugins" / "hypermemory" / "connection.json"
            connection.parent.mkdir(parents=True)
            connection.write_text(json.dumps({
                "Endpoint": "http://127.0.0.1:9999/",
                "Token": "installer-token",
                "RedactSecrets": False,
            }), encoding="utf-8")
            provider.initialize("session", hermes_home=home)
        self.assertEqual("http://127.0.0.1:9999", provider._endpoint)
        self.assertEqual("installer-token", provider._auth_token)
        self.assertFalse(provider._redact_secrets)

    def test_large_top_hit_is_truncated_instead_of_disappearing(self):
        provider = module.HyperMemoryProvider()
        with tempfile.TemporaryDirectory() as home:
            provider.initialize("session", hermes_home=home, agent_workspace="workspace")
            provider._request_json = lambda *_: [{
                "atom": {"versionId": "large", "content": "X" * 30_000, "occurredAt": "2026-01-01"},
                "citation": {}, "evidence": {}
            }]
            recalled = provider.prefetch("recover the long record")
        self.assertIn("X" * 100, recalled)
        self.assertIn("[RECORD TRUNCATED]", recalled)
        self.assertLessEqual(len(recalled), 24_100)

    def test_failed_write_stays_in_durable_outbox_and_retries(self):
        provider = module.HyperMemoryProvider()
        with tempfile.TemporaryDirectory() as home:
            provider.initialize("session", hermes_home=home, agent_context="primary")
            provider._request_json = lambda *_: None
            provider.sync_turn("remember this", "stored response", session_id="session")
            queued = list((Path(home) / "state" / "hypermemory-outbox").glob("*.json"))
            self.assertEqual(1, len(queued))
            self.assertEqual("remember this", json.loads(queued[0].read_text(encoding="utf-8"))["content"].splitlines()[1])

            provider._request_json = lambda *_: {"created": True}
            provider._flush_outbox()
            self.assertEqual([], list((Path(home) / "state" / "hypermemory-outbox").glob("*.json")))

    def test_non_primary_agent_context_does_not_write(self):
        provider = module.HyperMemoryProvider()
        with tempfile.TemporaryDirectory() as home:
            provider.initialize("subagent", hermes_home=home, agent_context="subagent")
            provider.sync_turn("cron prompt", "cron result", session_id="subagent")
            self.assertFalse((Path(home) / "state" / "hypermemory-outbox").exists())

    def test_obvious_secret_assignments_are_redacted_before_durable_storage(self):
        provider = module.HyperMemoryProvider()
        with tempfile.TemporaryDirectory() as home:
            provider.initialize("session", hermes_home=home, agent_context="primary")
            provider._request_json = lambda *_: None
            provider.sync_turn("api_key=super-secret-value", "Password: another-secret", session_id="session")
            queued = next((Path(home) / "state" / "hypermemory-outbox").glob("*.json"))
            payload = json.loads(queued.read_text(encoding="utf-8"))
            self.assertNotIn("super-secret-value", payload["content"])
            self.assertNotIn("another-secret", payload["content"])
            self.assertEqual("2", payload["metadata"]["privacy.redactions"])

    def test_bearer_private_key_jwt_token_card_and_uri_credentials_are_redacted(self):
        provider = module.HyperMemoryProvider()
        with tempfile.TemporaryDirectory() as home:
            provider.initialize("session", hermes_home=home, agent_context="primary")
            provider._request_json = lambda *_: None
            user = (
                "Authorization: Bearer bearer-secret-value\n"
                "token sk-1234567890abcdefghijklmnop\n"
                "jwt eyJabcdefghijk.abcdefghijklmnop.qrstuvwxyz12345\n"
                "card 4111 1111 1111 1111\n"
                "url https://alice:private-password@example.test/path"
            )
            assistant = "-----BEGIN PRIVATE KEY-----\nsecretmaterial\n-----END PRIVATE KEY-----"
            provider.sync_turn(user, assistant, session_id="session")
            queued = next((Path(home) / "state" / "hypermemory-outbox").glob("*.json"))
            payload = json.loads(queued.read_text(encoding="utf-8"))
            content = payload["content"]
            for secret in ("bearer-secret-value", "sk-123456", "eyJabcdefghijk", "4111 1111", "private-password", "secretmaterial"):
                self.assertNotIn(secret, content)
            self.assertGreaterEqual(int(payload["metadata"]["privacy.redactions"]), 6)
            self.assertEqual("restricted-redacted", payload["metadata"]["privacy.classification"])
            self.assertEqual("automatic-with-user-opt-out-v1", payload["metadata"]["privacy.capturePolicy"])

    def test_explicit_user_opt_out_does_not_persist_the_turn(self):
        provider = module.HyperMemoryProvider()
        with tempfile.TemporaryDirectory() as home:
            provider.initialize("session", hermes_home=home, agent_context="primary")
            provider.sync_turn("No guardes esto en la memoria: sorpresa", "Entendido", session_id="session")
            self.assertFalse((Path(home) / "state" / "hypermemory-outbox").exists())

    def test_capture_can_be_disabled_by_local_connection_policy(self):
        provider = module.HyperMemoryProvider()
        with tempfile.TemporaryDirectory() as home:
            connection = Path(home) / "plugins" / "hypermemory" / "connection.json"
            connection.parent.mkdir(parents=True)
            connection.write_text(json.dumps({"CaptureEnabled": False}), encoding="utf-8")
            provider.initialize("session", hermes_home=home, agent_context="primary")
            provider.sync_turn("recuerda esto", "respuesta", session_id="session")
            self.assertFalse((Path(home) / "state" / "hypermemory-outbox").exists())

    def test_successful_file_tool_is_hashed_as_verified_workspace_evidence(self):
        provider = module.HyperMemoryProvider()
        with tempfile.TemporaryDirectory() as home, tempfile.TemporaryDirectory() as workspace:
            artifact = Path(workspace) / "game" / "index.html"
            artifact.parent.mkdir()
            artifact.write_text("<h1>Verified game</h1>", encoding="utf-8")
            provider.initialize("session", hermes_home=home, agent_context="primary", agent_workspace=workspace)
            provider._request_json = lambda *_: None
            messages = [
                {"role": "user", "content": "crea el juego"},
                {"role": "assistant", "tool_calls": [{
                    "id": "call-write",
                    "function": {"name": "write_file", "arguments": json.dumps({"path": "game/index.html", "content": "..."})},
                }]},
                {"role": "tool", "tool_call_id": "call-write", "content": json.dumps({
                    "bytes_written": artifact.stat().st_size,
                    "verified": True,
                    "resolved_path": str(artifact),
                    "files_modified": [str(artifact)],
                })},
            ]
            provider.sync_turn("crea el juego", "He creado el juego.", session_id="session", messages=messages)
            queued = next((Path(home) / "state" / "hypermemory-outbox").glob("*.json"))
            payload = json.loads(queued.read_text(encoding="utf-8"))
            files = json.loads(payload["metadata"]["artifacts.verifiedFiles"])
            self.assertEqual("game/index.html", files[0]["path"])
            self.assertEqual(64, len(files[0]["sha256"]))
            self.assertEqual("workspace-file-hash", files[0]["verification"])

    def test_file_tool_cannot_verify_a_path_outside_the_active_workspace(self):
        provider = module.HyperMemoryProvider()
        with tempfile.TemporaryDirectory() as home, tempfile.TemporaryDirectory() as workspace, tempfile.TemporaryDirectory() as outside:
            artifact = Path(outside) / "private.txt"
            artifact.write_text("outside", encoding="utf-8")
            provider.initialize("session", hermes_home=home, agent_context="primary", agent_workspace=workspace)
            provider._request_json = lambda *_: None
            messages = [
                {"role": "user", "content": "write"},
                {"role": "assistant", "tool_calls": [{
                    "id": "call-write", "function": {"name": "write_file", "arguments": json.dumps({"path": str(artifact)})},
                }]},
                {"role": "tool", "tool_call_id": "call-write", "content": json.dumps({"resolved_path": str(artifact)})},
            ]
            provider.sync_turn("write", "done", session_id="session", messages=messages)
            queued = next((Path(home) / "state" / "hypermemory-outbox").glob("*.json"))
            payload = json.loads(queued.read_text(encoding="utf-8"))
            self.assertNotIn("artifacts.verifiedFiles", payload["metadata"])

    def test_terminal_exit_and_hermes_verification_are_stored_as_scoped_evidence(self):
        provider = module.HyperMemoryProvider()
        with tempfile.TemporaryDirectory() as home, tempfile.TemporaryDirectory() as workspace:
            provider.initialize("session", hermes_home=home, agent_context="primary", agent_workspace=workspace)
            provider._request_json = lambda *_: None
            messages = [
                {"role": "user", "content": "ejecuta las pruebas"},
                {"role": "assistant", "tool_calls": [{
                    "id": "call-test",
                    "function": {"name": "terminal", "arguments": json.dumps({
                        "command": "python -m pytest tests/test_game.py --token command-secret",
                        "workdir": workspace,
                    })},
                }]},
                {"role": "tool", "tool_call_id": "call-test", "content": json.dumps({
                    "output": "3 passed",
                    "exit_code": 0,
                    "error": None,
                    "verification_evidence": {
                        "status": "passed",
                        "kind": "test",
                        "scope": "targeted",
                        "canonical_command": "pytest",
                    },
                })},
            ]
            provider.sync_turn("ejecuta las pruebas", "Las pruebas pasaron.", session_id="session", messages=messages)
            queued = next((Path(home) / "state" / "hypermemory-outbox").glob("*.json"))
            payload = json.loads(queued.read_text(encoding="utf-8"))
            events = json.loads(payload["metadata"]["execution.events"])
            self.assertEqual("succeeded", events[0]["status"])
            self.assertEqual(0, events[0]["exitCode"])
            self.assertEqual("passed", events[0]["verification"]["status"])
            self.assertEqual("targeted", events[0]["verification"]["scope"])
            self.assertEqual(64, len(events[0]["outputSha256"]))
            self.assertNotIn("command-secret", events[0]["command"])
            self.assertIn("[REDACTED]", events[0]["command"])
            self.assertEqual(1, events[0]["privacyRedactions"])

    def test_background_terminal_spawn_is_not_misreported_as_completed_execution(self):
        provider = module.HyperMemoryProvider()
        with tempfile.TemporaryDirectory() as home, tempfile.TemporaryDirectory() as workspace:
            provider.initialize("session", hermes_home=home, agent_context="primary", agent_workspace=workspace)
            provider._request_json = lambda *_: None
            messages = [
                {"role": "user", "content": "inicia el servidor"},
                {"role": "assistant", "tool_calls": [{
                    "id": "call-server", "function": {"name": "terminal", "arguments": json.dumps({
                        "command": "python server.py", "background": True,
                    })},
                }]},
                {"role": "tool", "tool_call_id": "call-server", "content": json.dumps({"exit_code": 0, "session_id": "bg-1"})},
            ]
            provider.sync_turn("inicia el servidor", "Servidor iniciado.", session_id="session", messages=messages)
            queued = next((Path(home) / "state" / "hypermemory-outbox").glob("*.json"))
            payload = json.loads(queued.read_text(encoding="utf-8"))
            self.assertNotIn("execution.events", payload["metadata"])


if __name__ == "__main__":
    unittest.main()
