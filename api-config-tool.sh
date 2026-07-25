#!/bin/sh

# Interactive Codex / Claude Code configuration tool for macOS and Linux.
# Python enables model discovery. Configuration updates can also use Node.js.

set -f

PROGRAM_NAME=${0##*/}
PYTHON_BIN=
NODE_BIN=
CONFIG_RUNTIME=
WORK_DIR=
MODELS_TXT=

cleanup() {
    stty echo 2>/dev/null || true
    if [ -n "${WORK_DIR:-}" ] && [ -d "$WORK_DIR" ]; then
        rm -f "$WORK_DIR/models.txt"
        rmdir "$WORK_DIR" 2>/dev/null || true
    fi
}

trap cleanup EXIT
trap 'exit 130' HUP INT TERM

die() {
    printf '错误：%s\n' "$*" >&2
    exit 1
}

find_runtimes() {
    if command -v python3 >/dev/null 2>&1; then
        if python3 -c 'import sys; raise SystemExit(sys.version_info < (3, 8))' >/dev/null 2>&1; then
            PYTHON_BIN=python3
        fi
    fi
    if [ -z "$PYTHON_BIN" ] && command -v python >/dev/null 2>&1 && python -c 'import sys; raise SystemExit(sys.version_info < (3, 8))' >/dev/null 2>&1; then
        PYTHON_BIN=python
    fi

    if command -v node >/dev/null 2>&1 && node -e 'process.exit(Number(process.versions.node.split(".")[0]) < 16 ? 1 : 0)' >/dev/null 2>&1; then
        NODE_BIN=node
    fi

    if [ -n "$PYTHON_BIN" ]; then
        CONFIG_RUNTIME=python
    elif [ -n "$NODE_BIN" ]; then
        CONFIG_RUNTIME=node
    else
        die "需要 Python 3.8+ 或 Node.js 16+ 来安全修改 JSON 配置。"
    fi
}

run_python_helper() {
    "$PYTHON_BIN" - "$@" <<'PY'
import datetime
import json
import os
import re
import shlex
import shutil
import stat
import sys
import tempfile
import urllib.error
import urllib.request
from pathlib import Path
from urllib.parse import urlsplit, urlunsplit

try:
    import tomllib
except ImportError:
    tomllib = None


def fail(message):
    raise ValueError(message)


def toml_quote(value):
    escaped = (
        value.replace("\\", "\\\\")
        .replace('"', '\\"')
        .replace("\b", "\\b")
        .replace("\t", "\\t")
        .replace("\n", "\\n")
        .replace("\f", "\\f")
        .replace("\r", "\\r")
    )
    return '"' + escaped + '"'


def parse_toml_string(value):
    value = value.strip()
    if value.startswith('"'):
        match = re.match(r'^"(?:\\.|[^"\\])*"', value)
        if match:
            try:
                return json.loads(match.group(0))
            except Exception:
                return None
    if value.startswith("'"):
        end = value.find("'", 1)
        if end >= 1:
            return value[1:end]
    return None


def assignment_key(line):
    match = re.match(r'^\s*([A-Za-z0-9_-]+)\s*=', line)
    return match.group(1) if match else None


def section_header(line):
    match = re.match(r'^\s*\[([^\[\]]+)\]\s*(?:#.*)?(?:\r?\n)?$', line)
    return match.group(1).strip() if match else None


def uncommented_value(line):
    try:
        tail = line[line.index('=') + 1:]
    except ValueError:
        return ""

    quote = None
    escaped = False
    for index, char in enumerate(tail):
        if escaped:
            escaped = False
            continue
        if quote == '"' and char == "\\":
            escaped = True
            continue
        if quote:
            if char == quote:
                quote = None
            continue
        if char in ('"', "'"):
            quote = char
        elif char == '#':
            return tail[:index].strip()
    return tail.strip()


def replace_assignment_value(line, new_value):
    newline = ""
    body = line
    if body.endswith("\r\n"):
        body, newline = body[:-2], "\r\n"
    elif body.endswith("\n"):
        body, newline = body[:-1], "\n"

    equals = body.find('=')
    if equals < 0:
        return line
    tail = body[equals + 1:]
    leading_match = re.match(r'^[ \t]*', tail)
    leading = leading_match.group(0)

    quote = None
    escaped = False
    comment_at = None
    for index, char in enumerate(tail):
        if escaped:
            escaped = False
            continue
        if quote == '"' and char == "\\":
            escaped = True
            continue
        if quote:
            if char == quote:
                quote = None
            continue
        if char in ('"', "'"):
            quote = char
        elif char == '#':
            comment_at = index
            break

    before_comment = tail if comment_at is None else tail[:comment_at]
    spacing = before_comment[len(before_comment.rstrip(' \t')):]
    comment = "" if comment_at is None else tail[comment_at:]
    return body[:equals + 1] + leading + new_value + spacing + comment + newline


def read_top_level_strings(text):
    values = {}
    section = None
    for line in text.splitlines(True):
        header = section_header(line)
        if header is not None:
            section = header
            continue
        if section is not None:
            continue
        key = assignment_key(line)
        if key:
            parsed = parse_toml_string(uncommented_value(line))
            if parsed is not None:
                values[key] = parsed
    return values


def provider_from_header(header):
    prefix = "model_providers."
    if not header.startswith(prefix):
        return None
    tail = header[len(prefix):].strip()
    parsed = parse_toml_string(tail)
    return parsed if parsed is not None else tail


def read_codex_config(path):
    if not path.is_file():
        return "", ""
    try:
        text = path.read_text(encoding="utf-8-sig")
    except Exception:
        return "", ""

    if tomllib is not None:
        try:
            data = tomllib.loads(text)
            model = data.get("model") if isinstance(data.get("model"), str) else ""
            provider = data.get("model_provider")
            if not isinstance(provider, str) or not provider.strip():
                provider = "OpenAI"
            providers = data.get("model_providers")
            base_url = ""
            if isinstance(providers, dict):
                selected = providers.get(provider)
                if isinstance(selected, dict) and isinstance(selected.get("base_url"), str):
                    base_url = selected["base_url"]
                if not base_url:
                    for item in providers.values():
                        if isinstance(item, dict) and isinstance(item.get("base_url"), str):
                            base_url = item["base_url"]
                            break
            return base_url, model
        except Exception:
            pass

    top = read_top_level_strings(text)
    provider = top.get("model_provider", "OpenAI")
    model = top.get("model", "")
    selected_base = ""
    first_base = ""
    section = None
    for line in text.splitlines(True):
        header = section_header(line)
        if header is not None:
            section = provider_from_header(header)
            continue
        if section is not None and assignment_key(line) == "base_url":
            value = parse_toml_string(uncommented_value(line)) or ""
            if value and not first_base:
                first_base = value
            if value and section == provider:
                selected_base = value
    return selected_base or first_base, model


def read_json_object(path):
    if not path.is_file():
        return {}
    try:
        value = json.loads(path.read_text(encoding="utf-8-sig"))
        return value if isinstance(value, dict) else {}
    except Exception:
        return {}


def normalize_url(raw, target):
    raw = raw.strip()
    if not raw:
        fail("Base URL 不能为空。")
    if "://" not in raw:
        raw = "https://" + raw
    parsed = urlsplit(raw)
    if parsed.scheme.lower() not in ("http", "https") or not parsed.netloc:
        fail("Base URL 必须是有效的 HTTP 或 HTTPS 地址。")
    if parsed.query or parsed.fragment:
        fail("Base URL 不能包含查询参数或片段。")

    if target == "claude":
        return urlunsplit((parsed.scheme.lower(), parsed.netloc, "", "", ""))

    path = parsed.path.rstrip('/')
    if path.lower().endswith("/models"):
        path = path[:-len("/models")].rstrip('/')
    if not path.lower().endswith("/v1"):
        path += "/v1"
    return urlunsplit((parsed.scheme.lower(), parsed.netloc, path, "", ""))


def models_endpoint(raw):
    raw = raw.strip()
    if not raw:
        fail("Base URL 不能为空。")
    if "://" not in raw:
        raw = "https://" + raw
    parsed = urlsplit(raw)
    if parsed.scheme.lower() not in ("http", "https") or not parsed.netloc:
        fail("Base URL 必须是有效的 HTTP 或 HTTPS 地址。")
    if parsed.query or parsed.fragment:
        fail("Base URL 不能包含查询参数或片段。")
    path = parsed.path.rstrip('/')
    if path.lower().endswith("/models"):
        pass
    elif path.lower().endswith("/v1"):
        path += "/models"
    else:
        path += "/v1/models"
    return urlunsplit((parsed.scheme.lower(), parsed.netloc, path, "", ""))


def update_codex_text(text, model, base_url):
    newline = "\r\n" if "\r\n" in text else "\n"
    top = read_top_level_strings(text)
    provider = top.get("model_provider", "OpenAI").strip() or "OpenAI"
    lines = text.splitlines(True)

    section = None
    model_index = None
    provider_index = None
    for index, line in enumerate(lines):
        header = section_header(line)
        if header is not None:
            section = header
            continue
        if section is None:
            key = assignment_key(line)
            if key == "model" and model_index is None:
                model_index = index
            elif key == "model_provider" and provider_index is None:
                provider_index = index

    additions = []
    if provider_index is None:
        additions.append("model_provider = " + toml_quote(provider) + newline)
    if model_index is None:
        additions.append("model = " + toml_quote(model) + newline)
    else:
        lines[model_index] = replace_assignment_value(lines[model_index], toml_quote(model))
    if additions:
        lines[0:0] = additions

    target_start = None
    target_end = len(lines)
    for index, line in enumerate(lines):
        header = section_header(line)
        if header is None:
            continue
        current_provider = provider_from_header(header)
        if target_start is not None:
            target_end = index
            break
        if current_provider == provider:
            target_start = index

    if target_start is not None:
        base_index = None
        for index in range(target_start + 1, target_end):
            if assignment_key(lines[index]) == "base_url":
                base_index = index
                break
        if base_index is not None:
            lines[base_index] = replace_assignment_value(lines[base_index], toml_quote(base_url))
        else:
            if target_end > 0 and not lines[target_end - 1].endswith(("\n", "\r")):
                lines[target_end - 1] += newline
            lines.insert(target_end, "base_url = " + toml_quote(base_url) + newline)
    else:
        if lines and not lines[-1].endswith(("\n", "\r")):
            lines[-1] += newline
        if lines and lines[-1].strip():
            lines.append(newline)
        provider_key = provider if re.match(r'^[A-Za-z0-9_-]+$', provider) else toml_quote(provider)
        lines.extend([
            "[model_providers." + provider_key + "]" + newline,
            "name = " + toml_quote(provider) + newline,
            "base_url = " + toml_quote(base_url) + newline,
            "wire_api = \"responses\"" + newline,
            "requires_openai_auth = true" + newline,
        ])
    return "".join(lines)


def default_codex_text(model, base_url):
    return "\n".join([
        "# Generated by api-config-tool.sh",
        'model_provider = "OpenAI"',
        "model = " + toml_quote(model),
        "review_model = " + toml_quote(model),
        'model_reasoning_effort = "xhigh"',
        "disable_response_storage = true",
        'network_access = "enabled"',
        "",
        "[model_providers.OpenAI]",
        'name = "OpenAI"',
        "base_url = " + toml_quote(base_url),
        'wire_api = "responses"',
        "requires_openai_auth = true",
        "",
        "[features]",
        "goals = true",
        "",
    ])


def json_bytes(value):
    return (json.dumps(value, ensure_ascii=False, indent=2) + "\n").encode("utf-8")


def atomic_write(path, content, default_mode=0o600):
    parent_existed = path.parent.exists()
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    if not parent_existed:
        os.chmod(path.parent, 0o700)
    mode = stat.S_IMODE(path.stat().st_mode) if path.exists() else default_mode
    fd, temporary_name = tempfile.mkstemp(prefix="." + path.name + ".", dir=str(path.parent))
    try:
        if hasattr(os, "fchmod"):
            os.fchmod(fd, mode)
        else:
            os.chmod(temporary_name, mode)
        with os.fdopen(fd, "wb") as handle:
            handle.write(content)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary_name, path)
    except Exception:
        try:
            os.close(fd)
        except OSError:
            pass
        try:
            os.unlink(temporary_name)
        except OSError:
            pass
        raise


def backup_path(path):
    timestamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    candidate = Path(str(path) + ".bak." + timestamp)
    counter = 1
    while candidate.exists():
        candidate = Path(str(path) + ".bak." + timestamp + "." + str(counter))
        counter += 1
    shutil.copy2(path, candidate)
    return candidate


def transactional_write(files):
    changed = []
    for path, content in files:
        old = path.read_bytes() if path.exists() else None
        if old != content:
            changed.append((path, content, old, stat.S_IMODE(path.stat().st_mode) if path.exists() else 0o600))
    if not changed:
        return [], []

    backups = []
    for path, _content, old, _mode in changed:
        if old is not None:
            backups.append(backup_path(path))

    written = []
    try:
        for path, content, _old, _mode in changed:
            atomic_write(path, content)
            written.append(path)
    except Exception:
        for path, _content, old, mode in reversed(changed):
            if path not in written:
                continue
            if old is None:
                try:
                    path.unlink()
                except OSError:
                    pass
            else:
                atomic_write(path, old, mode)
        raise
    return [item[0] for item in changed], backups


def validate_values(base_url, api_key, model, target):
    base_url = normalize_url(base_url, target)
    api_key = api_key.strip()
    model = model.strip()
    if not api_key:
        fail("API Key 不能为空。")
    if not model:
        fail("模型名称不能为空。")
    if any(char in model for char in ("\0", "\r", "\n")):
        fail("模型名称不能包含换行符。")
    return base_url, api_key, model


def apply_codex(config_path, auth_path, base_url, api_key, model):
    base_url, api_key, model = validate_values(base_url, api_key, model, "codex")
    if config_path.exists():
        old_text = config_path.read_text(encoding="utf-8-sig")
        config = update_codex_text(old_text, model, base_url)
    else:
        config = default_codex_text(model, base_url)
    auth = read_json_object(auth_path)
    auth["OPENAI_API_KEY"] = api_key
    return transactional_write([
        (config_path, config.encode("utf-8")),
        (auth_path, json_bytes(auth)),
    ])


def apply_claude(settings_path, base_url, api_key, model):
    base_url, api_key, model = validate_values(base_url, api_key, model, "claude")
    root = read_json_object(settings_path)
    env = root.get("env")
    if not isinstance(env, dict):
        env = {}
        root["env"] = env
    env["ANTHROPIC_AUTH_TOKEN"] = api_key
    env["ANTHROPIC_BASE_URL"] = base_url
    env.setdefault("API_TIMEOUT_MS", "300000")
    root["model"] = model
    return transactional_write([(settings_path, json_bytes(root))])


def emit_shell(name, value):
    print(name + "=" + shlex.quote(value if isinstance(value, str) else ""))


def command_load(args):
    target = args[0]
    codex_config = Path(args[1]).expanduser()
    codex_auth = Path(args[2]).expanduser()
    claude_settings = Path(args[3]).expanduser()
    if target == "codex":
        base_url, model = read_codex_config(codex_config)
        auth = read_json_object(codex_auth)
        api_key = auth.get("OPENAI_API_KEY") if isinstance(auth.get("OPENAI_API_KEY"), str) else ""
    else:
        root = read_json_object(claude_settings)
        env = root.get("env") if isinstance(root.get("env"), dict) else {}
        base_url = env.get("ANTHROPIC_BASE_URL") if isinstance(env.get("ANTHROPIC_BASE_URL"), str) else ""
        api_key = env.get("ANTHROPIC_AUTH_TOKEN") if isinstance(env.get("ANTHROPIC_AUTH_TOKEN"), str) else ""
        model = root.get("model") if isinstance(root.get("model"), str) else ""
    emit_shell("CURRENT_BASE_URL", base_url)
    emit_shell("CURRENT_API_KEY", api_key)
    emit_shell("CURRENT_MODEL", model)


def command_apply(args):
    target = args[0]
    codex_config = Path(args[1]).expanduser()
    codex_auth = Path(args[2]).expanduser()
    claude_settings = Path(args[3]).expanduser()
    base_url, model = args[4:6]
    api_key = os.environ.get("API_CONFIG_API_KEY", "")
    if target == "codex":
        changed, backups = apply_codex(codex_config, codex_auth, base_url, api_key, model)
    else:
        changed, backups = apply_claude(claude_settings, base_url, api_key, model)
    if changed:
        for path in changed:
            print("已更新：" + str(path))
        for path in backups:
            print("备份文件：" + str(path))
    else:
        print("配置内容没有变化，无需写入。")


def command_fetch_models(args):
    endpoint = models_endpoint(args[0])
    api_key = os.environ.get("API_CONFIG_API_KEY", "")
    if not api_key:
        fail("API Key 不能为空。")
    request = urllib.request.Request(
        endpoint,
        headers={
            "Authorization": "Bearer " + api_key,
            "Accept": "application/json",
            "User-Agent": "api-config-tool.sh",
        },
    )
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            body = response.read().decode("utf-8-sig")
    except urllib.error.HTTPError as exc:
        body = exc.read().decode("utf-8", errors="replace")
        preview = " ".join(body.split())[:300]
        fail("获取模型失败（HTTP {}）：{}".format(exc.code, preview))
    except urllib.error.URLError as exc:
        fail("获取模型失败：{}".format(exc.reason))

    value = json.loads(body)
    items = value.get("data") if isinstance(value, dict) else value
    if not isinstance(items, list):
        fail("接口返回的内容不是有效的模型列表。")
    models = []
    seen = set()
    for item in items:
        model = item if isinstance(item, str) else item.get("id") if isinstance(item, dict) else None
        if not isinstance(model, str):
            continue
        model = model.strip()
        key = model.casefold()
        if model and "\n" not in model and "\r" not in model and key not in seen:
            seen.add(key)
            models.append(model)
    if not models:
        fail("接口请求成功，但没有解析到模型 ID。")
    for model in sorted(models, key=str.casefold):
        print(model)


def main():
    if len(sys.argv) < 2:
        fail("缺少内部命令。")
    command = sys.argv[1]
    args = sys.argv[2:]
    if command == "load" and len(args) == 4:
        command_load(args)
    elif command == "normalize" and len(args) == 2:
        print(normalize_url(args[1], args[0]))
    elif command == "fetch-models" and len(args) == 1:
        command_fetch_models(args)
    elif command == "apply" and len(args) == 6:
        command_apply(args)
    else:
        fail("无效的内部命令。")


try:
    main()
except Exception as exc:
    print("错误：" + str(exc), file=sys.stderr)
    sys.exit(1)
PY
}

run_node_helper() {
    "$NODE_BIN" - "$@" <<'JS'
const fs = require('fs');
const path = require('path');

function fail(message) {
    throw new Error(message);
}

function readText(file) {
    if (!fs.existsSync(file)) return '';
    return fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, '');
}

function readObject(file) {
    if (!fs.existsSync(file)) return {};
    try {
        const value = JSON.parse(readText(file));
        return value && typeof value === 'object' && !Array.isArray(value) ? value : {};
    } catch (_) {
        return {};
    }
}

function tomlQuote(value) {
    return '"' + value
        .replace(/\\/g, '\\\\')
        .replace(/"/g, '\\"')
        .replace(/\x08/g, '\\b')
        .replace(/\t/g, '\\t')
        .replace(/\n/g, '\\n')
        .replace(/\f/g, '\\f')
        .replace(/\r/g, '\\r') + '"';
}

function parseTomlString(value) {
    value = value.trim();
    const basic = value.match(/^"(?:\\.|[^"\\])*"/);
    if (basic) {
        try { return JSON.parse(basic[0]); } catch (_) { return null; }
    }
    const literal = value.match(/^'([^']*)'/);
    return literal ? literal[1] : null;
}

function assignmentKey(line) {
    const match = line.match(/^\s*([A-Za-z0-9_-]+)\s*=/);
    return match ? match[1] : null;
}

function sectionHeader(line) {
    const match = line.match(/^\s*\[([^\[\]]+)\]\s*(?:#.*)?$/);
    return match ? match[1].trim() : null;
}

function uncommentedValue(line) {
    const equals = line.indexOf('=');
    if (equals < 0) return '';
    const tail = line.slice(equals + 1);
    let quote = null;
    let escaped = false;
    for (let index = 0; index < tail.length; index += 1) {
        const char = tail[index];
        if (escaped) { escaped = false; continue; }
        if (quote === '"' && char === '\\') { escaped = true; continue; }
        if (quote) {
            if (char === quote) quote = null;
            continue;
        }
        if (char === '"' || char === "'") quote = char;
        else if (char === '#') return tail.slice(0, index).trim();
    }
    return tail.trim();
}

function replaceAssignmentValue(line, newValue) {
    const equals = line.indexOf('=');
    if (equals < 0) return line;
    const tail = line.slice(equals + 1);
    const leading = (tail.match(/^[ \t]*/) || [''])[0];
    let quote = null;
    let escaped = false;
    let commentAt = -1;
    for (let index = 0; index < tail.length; index += 1) {
        const char = tail[index];
        if (escaped) { escaped = false; continue; }
        if (quote === '"' && char === '\\') { escaped = true; continue; }
        if (quote) {
            if (char === quote) quote = null;
            continue;
        }
        if (char === '"' || char === "'") quote = char;
        else if (char === '#') { commentAt = index; break; }
    }
    const beforeComment = commentAt < 0 ? tail : tail.slice(0, commentAt);
    const spacing = (beforeComment.match(/[ \t]*$/) || [''])[0];
    const comment = commentAt < 0 ? '' : tail.slice(commentAt);
    return line.slice(0, equals + 1) + leading + newValue + spacing + comment;
}

function topLevelStrings(text) {
    const values = {};
    let section = null;
    for (const line of text.split(/\r?\n/)) {
        const header = sectionHeader(line);
        if (header !== null) { section = header; continue; }
        if (section !== null) continue;
        const key = assignmentKey(line);
        if (!key) continue;
        const value = parseTomlString(uncommentedValue(line));
        if (value !== null) values[key] = value;
    }
    return values;
}

function providerFromHeader(header) {
    const prefix = 'model_providers.';
    if (!header.startsWith(prefix)) return null;
    const tail = header.slice(prefix.length).trim();
    const parsed = parseTomlString(tail);
    return parsed === null ? tail : parsed;
}

function readCodex(file) {
    const text = readText(file);
    if (!text) return ['', ''];
    const top = topLevelStrings(text);
    const provider = top.model_provider || 'OpenAI';
    let selectedBase = '';
    let firstBase = '';
    let section = null;
    for (const line of text.split(/\r?\n/)) {
        const header = sectionHeader(line);
        if (header !== null) { section = providerFromHeader(header); continue; }
        if (section !== null && assignmentKey(line) === 'base_url') {
            const value = parseTomlString(uncommentedValue(line)) || '';
            if (value && !firstBase) firstBase = value;
            if (value && section === provider) selectedBase = value;
        }
    }
    return [selectedBase || firstBase, top.model || ''];
}

function normalizeUrl(raw, target) {
    raw = raw.trim();
    if (!raw) fail('Base URL 不能为空。');
    if (!raw.includes('://')) raw = 'https://' + raw;
    let value;
    try { value = new URL(raw); } catch (_) { fail('Base URL 必须是有效的 HTTP 或 HTTPS 地址。'); }
    if (!['http:', 'https:'].includes(value.protocol) || !value.host) {
        fail('Base URL 必须是有效的 HTTP 或 HTTPS 地址。');
    }
    if (value.search || value.hash) fail('Base URL 不能包含查询参数或片段。');
    if (target === 'claude') return value.protocol.toLowerCase() + '//' + value.host;
    let pathname = value.pathname.replace(/\/+$/, '');
    if (pathname.toLowerCase().endsWith('/models')) {
        pathname = pathname.slice(0, -'/models'.length).replace(/\/+$/, '');
    }
    if (!pathname.toLowerCase().endsWith('/v1')) pathname += '/v1';
    return value.protocol.toLowerCase() + '//' + value.host + pathname;
}

function updateCodexText(text, model, baseUrl) {
    const newline = text.includes('\r\n') ? '\r\n' : '\n';
    const top = topLevelStrings(text);
    const provider = (top.model_provider || 'OpenAI').trim() || 'OpenAI';
    let lines = text ? text.replace(/\r\n/g, '\n').split('\n') : [];
    if (lines.length && lines[lines.length - 1] === '') lines.pop();

    let section = null;
    let modelIndex = -1;
    let providerIndex = -1;
    lines.forEach((line, index) => {
        const header = sectionHeader(line);
        if (header !== null) { section = header; return; }
        if (section !== null) return;
        const key = assignmentKey(line);
        if (key === 'model' && modelIndex < 0) modelIndex = index;
        if (key === 'model_provider' && providerIndex < 0) providerIndex = index;
    });
    const additions = [];
    if (providerIndex < 0) additions.push('model_provider = ' + tomlQuote(provider));
    if (modelIndex < 0) additions.push('model = ' + tomlQuote(model));
    else lines[modelIndex] = replaceAssignmentValue(lines[modelIndex], tomlQuote(model));
    if (additions.length) lines = additions.concat(lines);

    let targetStart = -1;
    let targetEnd = lines.length;
    for (let index = 0; index < lines.length; index += 1) {
        const header = sectionHeader(lines[index]);
        if (header === null) continue;
        if (targetStart >= 0) { targetEnd = index; break; }
        if (providerFromHeader(header) === provider) targetStart = index;
    }
    if (targetStart >= 0) {
        let baseIndex = -1;
        for (let index = targetStart + 1; index < targetEnd; index += 1) {
            if (assignmentKey(lines[index]) === 'base_url') { baseIndex = index; break; }
        }
        if (baseIndex >= 0) lines[baseIndex] = replaceAssignmentValue(lines[baseIndex], tomlQuote(baseUrl));
        else lines.splice(targetEnd, 0, 'base_url = ' + tomlQuote(baseUrl));
    } else {
        if (lines.length && lines[lines.length - 1].trim()) lines.push('');
        const providerKey = /^[A-Za-z0-9_-]+$/.test(provider) ? provider : tomlQuote(provider);
        lines.push(
            '[model_providers.' + providerKey + ']',
            'name = ' + tomlQuote(provider),
            'base_url = ' + tomlQuote(baseUrl),
            'wire_api = "responses"',
            'requires_openai_auth = true'
        );
    }
    return lines.join(newline) + newline;
}

function defaultCodexText(model, baseUrl) {
    return [
        '# Generated by api-config-tool.sh',
        'model_provider = "OpenAI"',
        'model = ' + tomlQuote(model),
        'review_model = ' + tomlQuote(model),
        'model_reasoning_effort = "xhigh"',
        'disable_response_storage = true',
        'network_access = "enabled"',
        '',
        '[model_providers.OpenAI]',
        'name = "OpenAI"',
        'base_url = ' + tomlQuote(baseUrl),
        'wire_api = "responses"',
        'requires_openai_auth = true',
        '',
        '[features]',
        'goals = true',
        ''
    ].join('\n');
}

function jsonBuffer(value) {
    return Buffer.from(JSON.stringify(value, null, 2) + '\n', 'utf8');
}

function atomicWrite(file, content, defaultMode) {
    const directory = path.dirname(file);
    const parentExisted = fs.existsSync(directory);
    fs.mkdirSync(directory, {recursive: true, mode: 0o700});
    if (!parentExisted) fs.chmodSync(directory, 0o700);
    const mode = fs.existsSync(file) ? fs.statSync(file).mode & 0o777 : (defaultMode || 0o600);
    const temporary = path.join(directory, '.' + path.basename(file) + '.' + process.pid + '.' + Math.random().toString(16).slice(2));
    let descriptor;
    try {
        descriptor = fs.openSync(temporary, 'wx', mode);
        fs.writeFileSync(descriptor, content);
        fs.fsyncSync(descriptor);
        fs.closeSync(descriptor);
        descriptor = undefined;
        fs.renameSync(temporary, file);
    } catch (error) {
        if (descriptor !== undefined) {
            try { fs.closeSync(descriptor); } catch (_) {}
        }
        try { fs.unlinkSync(temporary); } catch (_) {}
        throw error;
    }
}

function backupPath(file) {
    const now = new Date();
    const pad = number => String(number).padStart(2, '0');
    const stamp = now.getFullYear() + pad(now.getMonth() + 1) + pad(now.getDate()) + '-' +
        pad(now.getHours()) + pad(now.getMinutes()) + pad(now.getSeconds());
    let candidate = file + '.bak.' + stamp;
    let counter = 1;
    while (fs.existsSync(candidate)) candidate = file + '.bak.' + stamp + '.' + counter++;
    fs.copyFileSync(file, candidate, fs.constants.COPYFILE_EXCL);
    fs.chmodSync(candidate, fs.statSync(file).mode & 0o777);
    return candidate;
}

function transactionalWrite(files) {
    const changed = files.map(item => {
        const exists = fs.existsSync(item[0]);
        const old = exists ? fs.readFileSync(item[0]) : null;
        return {file: item[0], content: item[1], old, mode: exists ? fs.statSync(item[0]).mode & 0o777 : 0o600};
    }).filter(item => item.old === null || !item.old.equals(item.content));
    if (!changed.length) return [[], []];
    const backups = changed.filter(item => item.old !== null).map(item => backupPath(item.file));
    const written = [];
    try {
        for (const item of changed) {
            atomicWrite(item.file, item.content, 0o600);
            written.push(item.file);
        }
    } catch (error) {
        for (const item of changed.slice().reverse()) {
            if (!written.includes(item.file)) continue;
            if (item.old === null) {
                try { fs.unlinkSync(item.file); } catch (_) {}
            } else atomicWrite(item.file, item.old, item.mode);
        }
        throw error;
    }
    return [changed.map(item => item.file), backups];
}

function validateValues(baseUrl, apiKey, model, target) {
    baseUrl = normalizeUrl(baseUrl, target);
    apiKey = apiKey.trim();
    model = model.trim();
    if (!apiKey) fail('API Key 不能为空。');
    if (!model) fail('模型名称不能为空。');
    if (/[\0\r\n]/.test(model)) fail('模型名称不能包含换行符。');
    return [baseUrl, apiKey, model];
}

function shellQuote(value) {
    return "'" + String(value || '').replace(/'/g, "'\"'\"'") + "'";
}

function commandLoad(args) {
    const target = args[0];
    let baseUrl = '';
    let apiKey = '';
    let model = '';
    if (target === 'codex') {
        [baseUrl, model] = readCodex(args[1]);
        const auth = readObject(args[2]);
        apiKey = typeof auth.OPENAI_API_KEY === 'string' ? auth.OPENAI_API_KEY : '';
    } else {
        const root = readObject(args[3]);
        const env = root.env && typeof root.env === 'object' && !Array.isArray(root.env) ? root.env : {};
        baseUrl = typeof env.ANTHROPIC_BASE_URL === 'string' ? env.ANTHROPIC_BASE_URL : '';
        apiKey = typeof env.ANTHROPIC_AUTH_TOKEN === 'string' ? env.ANTHROPIC_AUTH_TOKEN : '';
        model = typeof root.model === 'string' ? root.model : '';
    }
    console.log('CURRENT_BASE_URL=' + shellQuote(baseUrl));
    console.log('CURRENT_API_KEY=' + shellQuote(apiKey));
    console.log('CURRENT_MODEL=' + shellQuote(model));
}

function commandApply(args) {
    const target = args[0];
    let [baseUrl, apiKey, model] = validateValues(args[4], process.env.API_CONFIG_API_KEY || '', args[5], target);
    let result;
    if (target === 'codex') {
        const config = fs.existsSync(args[1]) ? updateCodexText(readText(args[1]), model, baseUrl) : defaultCodexText(model, baseUrl);
        const auth = readObject(args[2]);
        auth.OPENAI_API_KEY = apiKey;
        result = transactionalWrite([
            [args[1], Buffer.from(config, 'utf8')],
            [args[2], jsonBuffer(auth)]
        ]);
    } else {
        const root = readObject(args[3]);
        if (!root.env || typeof root.env !== 'object' || Array.isArray(root.env)) root.env = {};
        root.env.ANTHROPIC_AUTH_TOKEN = apiKey;
        root.env.ANTHROPIC_BASE_URL = baseUrl;
        if (root.env.API_TIMEOUT_MS === undefined || root.env.API_TIMEOUT_MS === null) root.env.API_TIMEOUT_MS = '300000';
        root.model = model;
        result = transactionalWrite([[args[3], jsonBuffer(root)]]);
    }
    const [changed, backups] = result;
    if (!changed.length) console.log('配置内容没有变化，无需写入。');
    else {
        changed.forEach(file => console.log('已更新：' + file));
        backups.forEach(file => console.log('备份文件：' + file));
    }
}

function main() {
    const args = process.argv.slice(2);
    const command = args.shift();
    if (command === 'load' && args.length === 4) commandLoad(args);
    else if (command === 'normalize' && args.length === 2) console.log(normalizeUrl(args[1], args[0]));
    else if (command === 'apply' && args.length === 6) commandApply(args);
    else fail('无效的内部命令。');
}

try {
    main();
} catch (error) {
    console.error('错误：' + error.message);
    process.exit(1);
}
JS
}

run_helper() {
    if [ "$CONFIG_RUNTIME" = "python" ]; then
        run_python_helper "$@"
    else
        run_node_helper "$@"
    fi
}

prompt_value() {
    PROMPT_LABEL=$1
    PROMPT_DEFAULT=$2
    if [ -n "$PROMPT_DEFAULT" ]; then
        printf '%s [%s]: ' "$PROMPT_LABEL" "$PROMPT_DEFAULT"
    else
        printf '%s: ' "$PROMPT_LABEL"
    fi
    IFS= read -r REPLY || exit 0
    if [ -z "$REPLY" ]; then
        REPLY=$PROMPT_DEFAULT
    fi
}

prompt_secret() {
    PROMPT_HAS_CURRENT=$1
    if [ "$PROMPT_HAS_CURRENT" = "yes" ]; then
        printf 'API Key [直接回车保留现有值]: '
    else
        printf 'API Key: '
    fi
    if [ -t 0 ]; then
        stty -echo
        IFS= read -r REPLY || {
            stty echo
            exit 0
        }
        stty echo
        printf '\n'
    else
        IFS= read -r REPLY || exit 0
    fi
}

ask_yes_no() {
    ASK_LABEL=$1
    ASK_DEFAULT=$2
    if [ "$ASK_DEFAULT" = "yes" ]; then
        printf '%s [Y/n]: ' "$ASK_LABEL"
    else
        printf '%s [y/N]: ' "$ASK_LABEL"
    fi
    IFS= read -r REPLY || exit 0
    case "$REPLY" in
        y|Y|yes|YES|Yes) return 0 ;;
        n|N|no|NO|No) return 1 ;;
        '') [ "$ASK_DEFAULT" = "yes" ] ;;
        *) return 1 ;;
    esac
}

fetch_models_with_python() {
    FETCH_BASE_URL=$1
    FETCH_API_KEY=$2
    : > "$MODELS_TXT"
    printf '检测到 Python，正在获取模型列表...\n'
    if ! API_CONFIG_API_KEY=$FETCH_API_KEY run_python_helper fetch-models "$FETCH_BASE_URL" > "$MODELS_TXT"; then
        return 1
    fi
    [ -s "$MODELS_TXT" ]
}

choose_model() {
    CHOOSE_CURRENT=$1
    CHOOSE_COUNT=$(wc -l < "$MODELS_TXT" | tr -d '[:space:]')
    printf '\n可用模型（%s 个）：\n' "$CHOOSE_COUNT"
    CHOOSE_INDEX=1
    while IFS= read -r CHOOSE_NAME; do
        printf '  %3d) %s\n' "$CHOOSE_INDEX" "$CHOOSE_NAME"
        CHOOSE_INDEX=$((CHOOSE_INDEX + 1))
    done < "$MODELS_TXT"

    while :; do
        if [ -n "$CHOOSE_CURRENT" ]; then
            printf '输入序号或模型名称 [%s]: ' "$CHOOSE_CURRENT"
        else
            printf '输入序号或模型名称 [1]: '
        fi
        IFS= read -r CHOOSE_REPLY || exit 0
        if [ -z "$CHOOSE_REPLY" ]; then
            if [ -n "$CHOOSE_CURRENT" ]; then
                MODEL=$CHOOSE_CURRENT
            else
                MODEL=$(sed -n '1p' "$MODELS_TXT")
            fi
            return 0
        fi
        case "$CHOOSE_REPLY" in
            *[!0-9]*) MODEL=$CHOOSE_REPLY; return 0 ;;
            *)
                if [ "$CHOOSE_REPLY" -ge 1 ] 2>/dev/null && [ "$CHOOSE_REPLY" -le "$CHOOSE_COUNT" ] 2>/dev/null; then
                    MODEL=$(sed -n "${CHOOSE_REPLY}p" "$MODELS_TXT")
                    return 0
                fi
                printf '请输入 1 到 %s 之间的序号，或直接输入模型名称。\n' "$CHOOSE_COUNT"
                ;;
        esac
    done
}

prompt_model_manually() {
    MANUAL_CURRENT=$1
    while :; do
        prompt_value "模型名称" "$MANUAL_CURRENT"
        MODEL=$REPLY
        if [ -n "$MODEL" ]; then
            return 0
        fi
        printf '模型名称不能为空。\n'
    done
}

configure_target() {
    TARGET=$1
    if [ "$TARGET" = "codex" ]; then
        TARGET_LABEL="Codex"
        TARGET_PATHS="$CODEX_CONFIG_PATH\n$CODEX_AUTH_PATH"
    else
        TARGET_LABEL="Claude Code"
        TARGET_PATHS=$CLAUDE_SETTINGS_PATH
    fi

    printf '\n--- 配置 %s ---\n' "$TARGET_LABEL"
    printf '配置文件：\n%b\n\n' "$TARGET_PATHS"

    if ! LOAD_RESULT=$(run_helper load "$TARGET" "$CODEX_CONFIG_PATH" "$CODEX_AUTH_PATH" "$CLAUDE_SETTINGS_PATH"); then
        printf '读取现有配置失败。\n' >&2
        return 1
    fi
    eval "$LOAD_RESULT"

    if [ -n "$CURRENT_BASE_URL$CURRENT_MODEL$CURRENT_API_KEY" ]; then
        printf '已读取现有配置：\n'
        printf '  Base URL: %s\n' "${CURRENT_BASE_URL:-未配置}"
        printf '  模型: %s\n' "${CURRENT_MODEL:-未配置}"
        if [ -n "$CURRENT_API_KEY" ]; then
            printf '  API Key: 已配置（隐藏）\n'
        else
            printf '  API Key: 未配置\n'
        fi
        printf '\n'
    fi

    while :; do
        prompt_value "Base URL（可省略 https://）" "$CURRENT_BASE_URL"
        BASE_URL=$REPLY
        if [ -n "$BASE_URL" ]; then
            break
        fi
        printf 'Base URL 不能为空。\n'
    done

    while :; do
        if [ -n "$CURRENT_API_KEY" ]; then
            prompt_secret yes
            if [ -z "$REPLY" ]; then
                API_KEY=$CURRENT_API_KEY
            else
                API_KEY=$REPLY
            fi
        else
            prompt_secret no
            API_KEY=$REPLY
        fi
        if [ -n "$API_KEY" ]; then
            break
        fi
        printf 'API Key 不能为空。\n'
    done

    if [ -n "$PYTHON_BIN" ]; then
        if fetch_models_with_python "$BASE_URL" "$API_KEY"; then
            choose_model "$CURRENT_MODEL"
        else
            printf '模型列表获取失败，改为手动输入。\n'
            prompt_model_manually "$CURRENT_MODEL"
        fi
    else
        printf '未检测到 Python，模型名称将手动输入。\n'
        prompt_model_manually "$CURRENT_MODEL"
    fi

    if ! NORMALIZED_URL=$(run_helper normalize "$TARGET" "$BASE_URL"); then
        return 1
    fi

    printf '\n即将写入 %s：\n' "$TARGET_LABEL"
    printf '  Base URL: %s\n' "$NORMALIZED_URL"
    printf '  模型: %s\n' "$MODEL"
    printf '  API Key: 已填写（隐藏）\n'
    if ! ask_yes_no "确认写入配置" no; then
        printf '已取消。\n'
        return 0
    fi

    if APPLY_RESULT=$(API_CONFIG_API_KEY=$API_KEY run_helper apply "$TARGET" "$CODEX_CONFIG_PATH" "$CODEX_AUTH_PATH" "$CLAUDE_SETTINGS_PATH" "$NORMALIZED_URL" "$MODEL"); then
        printf '\n%s 配置完成。\n%s\n' "$TARGET_LABEL" "$APPLY_RESULT"
    else
        printf '%s 配置写入失败。\n' "$TARGET_LABEL" >&2
        return 1
    fi
}

show_help() {
    cat <<EOF
用法：$PROGRAM_NAME [选项]

不带参数时显示交互式主菜单。

选项：
  --codex    直接配置 Codex
  --claude   直接配置 Claude Code
  -h, --help 显示帮助

运行时：Python 3.8+ 或 Node.js 16+
模型列表：检测到 Python 时自动获取，否则手动输入
EOF
}

main_menu() {
    while :; do
        printf '\n========================================\n'
        printf ' API 配置工具（Codex / Claude Code）\n'
        printf '========================================\n'
        printf '  1) 配置 Codex\n'
        printf '  2) 配置 Claude Code\n'
        printf '  0) 退出\n'
        printf '请选择 [0-2]: '
        IFS= read -r MENU_CHOICE || exit 0
        case "$MENU_CHOICE" in
            1) configure_target codex || true ;;
            2) configure_target claude || true ;;
            0|'') exit 0 ;;
            *) printf '无效选项，请重新选择。\n' ;;
        esac
    done
}

[ -n "${HOME:-}" ] || die "HOME 环境变量未设置。"
find_runtimes

if [ -n "$PYTHON_BIN" ]; then
    WORK_DIR=$(mktemp -d "${TMPDIR:-/tmp}/api-config-tool.XXXXXX") || die "无法创建临时目录。"
    MODELS_TXT="$WORK_DIR/models.txt"
fi

CODEX_CONFIG_ROOT=${CODEX_HOME:-"$HOME/.codex"}
CLAUDE_CONFIG_ROOT=${CLAUDE_CONFIG_DIR:-"$HOME/.claude"}
CODEX_CONFIG_PATH="$CODEX_CONFIG_ROOT/config.toml"
CODEX_AUTH_PATH="$CODEX_CONFIG_ROOT/auth.json"
CLAUDE_SETTINGS_PATH="$CLAUDE_CONFIG_ROOT/settings.json"

case "${1:-}" in
    '') main_menu ;;
    --codex) configure_target codex ;;
    --claude) configure_target claude ;;
    -h|--help) show_help ;;
    *) show_help >&2; exit 2 ;;
esac
