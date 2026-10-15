import pytest

import config


@pytest.fixture(autouse=True)
def no_llm_by_default(monkeypatch):
    """A developer's Backend/.env may switch the LLM operator on; tests must
    never call a model. Tests of the operator set the mode themselves."""
    monkeypatch.setattr(config, "LLM_MUTATION_MODE", "off")
