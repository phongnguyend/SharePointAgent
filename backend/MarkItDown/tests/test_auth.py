import importlib.util
import os
from pathlib import Path
import unittest
from unittest.mock import patch
from fastapi.testclient import TestClient

SPEC = importlib.util.spec_from_file_location('converter', Path(__file__).parents[1] / 'app' / 'main.py')
converter = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(converter)


class AuthenticationTests(unittest.TestCase):
    def test_upload_rejected_before_validation_without_key(self):
        with patch.dict(os.environ, {'MARKITDOWN_API_KEY': 'test-key'}), TestClient(converter.app) as client:
            self.assertEqual(401, client.post('/convert').status_code)
            self.assertEqual(401, client.post('/convert', headers={'X-Api-Key': 'wrong'}).status_code)
            self.assertEqual(422, client.post('/convert', headers={'X-Api-Key': 'test-key'}).status_code)
            self.assertEqual(200, client.get('/health').status_code)

    def test_local_mode_can_run_without_authentication(self):
        with patch.dict(os.environ, {'MARKITDOWN_API_KEY': ''}), TestClient(converter.app) as client:
            self.assertEqual(422, client.post('/convert').status_code)


if __name__ == '__main__':
    unittest.main()
