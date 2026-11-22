import { useEffect, useMemo, useState } from "react";
import { Download, Loader2, Search, Sparkles, Trash2, X } from "lucide-react";
import {
  api,
  desktop,
  errorText,
  type InstalledSkill,
  type SkillSearchResult,
} from "@/lib/api";
import { Button } from "./ui/button";
import { Input } from "./ui/input";
import { ConfirmDialog } from "./ConfirmDialog";

export function SkillsPage() {
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<SkillSearchResult[]>([]);
  const [searched, setSearched] = useState(false);
  const [installed, setInstalled] = useState<InstalledSkill[]>([]);
  const [target, setTarget] = useState("codex");
  const [loading, setLoading] = useState(false);
  const [busy, setBusy] = useState("");
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [remove, setRemove] = useState<InstalledSkill | null>(null);

  async function refreshInstalled() {
    if (!desktop) return;
    try {
      setInstalled(await api.skillsList());
    } catch (reason) {
      setError(errorText(reason));
    }
  }
  useEffect(() => {
    void refreshInstalled();
  }, []);

  async function search() {
    if (query.trim().length < 2) {
      setError("请输入至少 2 个字符的搜索词。");
      return;
    }
    setLoading(true);
    setError("");
    setNotice("");
    try {
      setResults(await api.skillsSearch(query));
      setSearched(true);
    } catch (reason) {
      setError(errorText(reason));
    } finally {
      setLoading(false);
    }
  }

  async function install(skill: SkillSearchResult) {
    setBusy(skill.id);
    setError("");
    setNotice("");
    try {
      await api.skillsInstall(skill.source, skill.skillId, skill.name, target);
      await refreshInstalled();
      setNotice(
        `已安装「${skill.name}」到 ${target === "codex" ? "Codex" : "Claude Code"}。`,
      );
    } catch (reason) {
      setError(errorText(reason));
    } finally {
      setBusy("");
    }
  }

  async function uninstall() {
    if (!remove) return;
    setBusy(remove.name);
    try {
      await api.skillsRemove(remove.app, remove.name);
      setInstalled((old) => old.filter((item) => item.path !== remove.path));
      setRemove(null);
      setNotice(`已卸载「${remove.name}」。`);
    } catch (reason) {
      setError(errorText(reason));
    } finally {
      setBusy("");
    }
  }

  function clearResults() {
    setResults([]);
    setSearched(false);
  }

  const installedKeys = useMemo(
    () =>
      new Set(
        installed
          .filter((item) => item.app === target)
          .map((item) => item.name.toLowerCase()),
      ),
    [installed, target],
  );
  return (
    <div className="tool-page skills-page">
      <div className="tool-page-header">
        <div>
          <h1>Skills 管理</h1>
          <p>搜索 skills.sh 的公开 Skills，并安装到 Codex 或 Claude Code。</p>
        </div>
        <label className="skill-target">
          <span>安装到</span>
          <select
            value={target}
            onChange={(event) => setTarget(event.target.value)}
          >
            <option value="codex">Codex</option>
            <option value="claude">Claude Code</option>
          </select>
        </label>
      </div>
      {!desktop && (
        <div className="preview-notice">
          界面预览 · 在桌面应用中才能读取和安装本机 Skills
        </div>
      )}
      {error && <div className="error-message page-error">{error}</div>}
      {notice && <div className="success-message">{notice}</div>}
      <div className="skills-layout">
        <section className="skills-section skills-results-section">
          <div className="skills-search">
            <Search size={17} />
            <Input
              aria-label="搜索 Skills"
              placeholder="搜索名称或用途，例如 frontend、testing…"
              value={query}
              onChange={(event) => setQuery(event.target.value)}
              onKeyDown={(event) => {
                if (event.key === "Enter") void search();
              }}
            />
            <Button
              onClick={() => void search()}
              disabled={loading || !desktop}
            >
              {loading ? (
                <Loader2 size={15} className="animate-spin" />
              ) : (
                <Search size={15} />
              )}
              搜索
            </Button>
          </div>
          <div className="section-heading">
            <h2>搜索结果</h2>
            <div className="skills-result-actions">
              <span>{results.length} 个</span>
              {!!results.length && (
                <Button
                  variant="quiet"
                  size="compact"
                  aria-label="清除搜索结果"
                  onClick={clearResults}
                >
                  <X size={14} />
                  清除结果
                </Button>
              )}
            </div>
          </div>
          {results.length ? (
            <div className="skills-grid">
              {results.map((skill) => {
                const installName = (
                  skill.skillId.split("/").pop() ?? skill.name
                ).replace(/[^a-zA-Z0-9_-]/g, "-");
                const installedHere = installedKeys.has(
                  installName.toLowerCase(),
                );
                return (
                  <article className="skill-card" key={skill.id}>
                    <div className="skill-card-icon">
                      <Sparkles size={18} />
                    </div>
                    <div className="skill-card-body">
                      <h3>{skill.name}</h3>
                      <p>
                        {skill.source} ·{" "}
                        {skill.installs.toLocaleString("zh-CN")} 次安装
                      </p>
                      <code>{skill.skillId}</code>
                    </div>
                    <Button
                      variant={installedHere ? "neutral" : "quiet"}
                      size="compact"
                      disabled={installedHere || busy === skill.id || !desktop}
                      onClick={() => void install(skill)}
                    >
                      {busy === skill.id ? (
                        <Loader2 size={14} className="animate-spin" />
                      ) : (
                        <Download size={14} />
                      )}
                      {installedHere ? "已安装" : "安装"}
                    </Button>
                  </article>
                );
              })}
            </div>
          ) : (
            <div className="skills-empty">
              {searched
                ? "没有找到匹配的 Skills。"
                : query.trim()
                  ? "搜索结果已清除。"
                  : "输入关键词搜索可用 Skills。"}
            </div>
          )}
        </section>
        <section className="skills-section skills-installed-section">
          <div className="section-heading">
            <h2>已安装</h2>
            <span>{installed.length} 个</span>
          </div>
          {installed.length ? (
            <div className="installed-skills">
              {installed.map((skill) => (
                <div
                  className="installed-skill"
                  key={`${skill.app}:${skill.path}`}
                >
                  <div>
                    <strong>{skill.name}</strong>
                    <span>
                      {skill.app === "codex" ? "Codex" : "Claude Code"} ·{" "}
                      {skill.path}
                    </span>
                  </div>
                  <Button
                    variant="ghost"
                    size="icon-compact"
                    aria-label={`卸载 ${skill.name}`}
                    onClick={() => setRemove(skill)}
                    disabled={busy === skill.name}
                  >
                    <Trash2 size={15} />
                  </Button>
                </div>
              ))}
            </div>
          ) : (
            <div className="skills-empty">还没有安装 Skills。</div>
          )}
        </section>
      </div>
      {remove && (
        <ConfirmDialog
          title={`卸载「${remove.name}」？`}
          description={`将从 ${remove.app === "codex" ? "Codex" : "Claude Code"} 的 Skills 目录删除该文件夹。`}
          confirm="卸载"
          destructive
          busy={busy === remove.name}
          onCancel={() => setRemove(null)}
          onConfirm={() => void uninstall()}
        />
      )}
    </div>
  );
}
