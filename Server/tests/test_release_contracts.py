import os
from pathlib import Path
import subprocess
import sys


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]


def test_python_ci_enforces_coverage_floor():
    workflow = (REPOSITORY_ROOT / ".github" / "workflows" / "python-tests.yml").read_text(
        encoding="utf-8"
    )

    assert "--cov-fail-under=62" in workflow


def test_unity_ci_fails_when_license_is_unavailable():
    for workflow_name in ("unity-tests.yml", "e2e-bridge.yml"):
        workflow = (
            REPOSITORY_ROOT / ".github" / "workflows" / workflow_name
        ).read_text(encoding="utf-8")
        missing_license_branch = workflow.split("Unity license secrets", 1)[1]
        assert "exit 1" in missing_license_branch


def test_generated_editor_project_restores_attributed_type_catalog():
    targets = (
        REPOSITORY_ROOT
        / "TestProjects"
        / "UnityMCPTests"
        / "Directory.Build.targets"
    ).read_text(encoding="utf-8")

    assert "MCPForUnity.Editor" in targets
    assert "AttributedTypeCatalog.cs" in targets


def test_server_and_cli_report_package_version():
    environment = os.environ.copy()
    environment["DISABLE_TELEMETRY"] = "true"
    result = subprocess.run(
        [
            sys.executable,
            "-c",
            (
                "from cli import __version__; "
                "from core.telemetry import get_package_version; "
                "from main import create_mcp_server; "
                "server = create_mcp_server(True); "
                "assert __version__ == get_package_version() == server.version"
            ),
        ],
        cwd=REPOSITORY_ROOT / "Server",
        env=environment,
        check=False,
        capture_output=True,
        text=True,
    )

    assert result.returncode == 0, result.stderr
