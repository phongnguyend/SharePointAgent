"""Fetch only the two pinned inference checkpoints, inside the remote build."""
from huggingface_hub import snapshot_download

MODELS = {
    "english": ("convaiinnovations/laya", "7b928d828b7b0e022f929d9bd2e44165aa270148"),
    "multilingual": ("convaiinnovations/laya-multilingual", "1720e3e3357cfe1e281542e223f8273b0890ca34"),
}


def main():
    for name, (repository, revision) in MODELS.items():
        snapshot_download(
            repo_id=repository,
            revision=revision,
            local_dir=f"/opt/models/{name}",
            allow_patterns=["rl_agent_config.json", "model.safetensors", "tokenizer/*", "encoder/*", "*LICENSE*"],
        )


if __name__ == "__main__":
    main()
