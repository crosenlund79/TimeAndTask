import { test } from "node:test";
import assert from "node:assert/strict";
import { escapeHtml, renderMarkdown } from "../app/markdown.js";

test("formatting", () => {
  assert.equal(renderMarkdown("Hello **world** and *you*"), "<p>Hello <strong>world</strong> and <em>you</em></p>");
  assert.equal(renderMarkdown("- a\n- b\n\n1. c"), "<ul><li>a</li><li>b</li></ul><ol><li>c</li></ol>");
  assert.equal(renderMarkdown("line one\nline two"), "<p>line one<br>line two</p>");
  assert.equal(renderMarkdown("Use `RealAppTests`"), "<p>Use <code>RealAppTests</code></p>");
  assert.equal(renderMarkdown("```\n<b>x</b>\n```"), "<pre><code>&lt;b&gt;x&lt;/b&gt;</code></pre>");
  assert.equal(renderMarkdown("[CI run](https://github.com/x/y/actions/runs/1)"),
    '<p><a href="https://github.com/x/y/actions/runs/1" target="_blank" rel="noopener noreferrer">CI run</a></p>');
});

test("bare URLs become links; punctuation after them does not", () => {
  assert.equal(renderMarkdown("See https://example.com/a?b=1&c=2."),
    '<p>See <a href="https://example.com/a?b=1&amp;c=2" target="_blank" rel="noopener noreferrer">https://example.com/a?b=1&amp;c=2</a>.</p>');
  assert.equal(renderMarkdown("[docs](https://example.com/x) and `https://not.a/link`"),
    '<p><a href="https://example.com/x" target="_blank" rel="noopener noreferrer">docs</a> and <code>https://not.a/link</code></p>');
});

test("never produces markup from user text", () => {
  const attacks = [
    "<script>alert(1)</script>",
    "<img src=x onerror=alert(1)>",
    "[click](javascript:alert(1))",
    '[x](https://a.com" onmouseover="alert(1))',
    "**<svg onload=alert(1)>**",
    "`<iframe>`",
    "<a href='javascript:alert(1)'>a</a>",
    "https://x.com/\"onmouseover=\"alert(1)",
    "https://x.com/<script>alert(1)</script>",
  ];
  for (const attack of attacks) {
    const html = renderMarkdown(attack);
    assert.doesNotMatch(html, /<(script|img|svg|iframe)/i, attack);
    assert.doesNotMatch(html, /href="javascript/i, attack);
    assert.doesNotMatch(html, /\son\w+="/i, attack);
  }
  assert.equal(escapeHtml(`<>&"'`), "&lt;&gt;&amp;&quot;&#39;");
});
