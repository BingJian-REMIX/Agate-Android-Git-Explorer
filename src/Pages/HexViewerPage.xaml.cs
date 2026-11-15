using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.ApplicationModel;
using Pebble.Models;
using Pebble.Services;

namespace Pebble;

/// <summary>
/// 十六进制查看器：只读 / 编辑双模式。
/// 渲染与交互（点击字节 → 数字键盘 → 修改）全部在 WebView 内的 JS 完成，
/// C# 仅通过 EvaluateJavaScriptAsync 做单向调用（取回编辑结果、触发搜索），
/// 避免依赖脆弱的 JS→C# 桥。保存时用 FileStream.Seek 精准写回，并先备份原文件。
/// </summary>
public partial class HexViewerPage : ContentPage
{
    private const long MaxHexBytes = 1L * 1024 * 1024;   // 1 MB：WebView 按字节铺 span，过大 HTML 会爆

    private readonly FileItem _item;
    private byte[] _data = Array.Empty<byte>();

    public HexViewerPage(FileItem item)
    {
        InitializeComponent();

        _item = item;
        Title = item.Name;
        TitleLabel.Text = item.Name;

        try
        {
            _data = File.ReadAllBytes(item.FullPath);
            var dark = Application.Current?.RequestedTheme == AppTheme.Dark;
            View.Source = new HtmlWebViewSource { Html = BuildHexHtml(_data, dark) };
        }
        catch (Exception ex)
        {
            AppLog.Error("hex-load", ex);
            View.Source = new HtmlWebViewSource { Html = BuildErrorHtml(ex.Message) };
        }
    }

    // ---- 顶部交互 ----

    private async void OnEditToggled(object? sender, ToggledEventArgs e)
    {
        var edit = e.Value;
        ModeLabel.Text = edit ? "编辑模式" : "只读模式";
        SaveButton.IsVisible = edit;
        try
        {
            await View.EvaluateJavaScriptAsync("__hex.setEditable(" + (edit ? "true" : "false") + ")");
        }
        catch (Exception ex)
        {
            AppLog.Error("hex-mode", ex);
        }
    }

    private async void OnSearchClicked(object? sender, EventArgs e)
    {
        var term = SearchEntry.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(term)) return;
        try
        {
            await View.EvaluateJavaScriptAsync("__hex.doSearch(" + JsonSerializer.Serialize(term) + ")");
        }
        catch (Exception ex)
        {
            AppLog.Error("hex-search", ex);
        }
    }

    private async void OnNextMatch(object? sender, EventArgs e)
    {
        try
        {
            await View.EvaluateJavaScriptAsync("__hex.next()");
        }
        catch (Exception ex)
        {
            AppLog.Error("hex-next", ex);
        }
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        try
        {
            // 1) 取回 WebView 内记录的编辑（offset -> 新字节十六进制）
            var raw = await View.EvaluateJavaScriptAsync("__hex.getEdits()");
            var json = UnwrapScriptResult(raw);
            var edits = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                        ?? new Dictionary<string, string>();

            if (edits.Count == 0)
            {
                await this.SafeAlertAsync("无需保存", "没有修改的字节。", "好");
                return;
            }

            // 2) 保存前先备份原文件（带时间戳，不覆盖上一次备份）
            var stamp = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            var backup = _item.FullPath + ".pebble-bak-" + stamp;
            File.Copy(_item.FullPath, backup, overwrite: false);

            // 3) 用 FileStream.Seek 精准写回每一个被修改的字节
            using (var fs = new FileStream(_item.FullPath, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                foreach (var kv in edits)
                {
                    if (!int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var off)
                        || off < 0 || off >= _data.Length)
                        continue;
                    if (!byte.TryParse(kv.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var val))
                        continue;

                    fs.Seek(off, SeekOrigin.Begin);
                    fs.WriteByte(val);
                }
                fs.Flush();
            }

            // 4) 同步内存镜像，并通知 WebView 清除"已修改"高亮
            _data = await File.ReadAllBytesAsync(_item.FullPath);
            await View.EvaluateJavaScriptAsync("__hex.afterSave()");

            await this.SafeAlertAsync("已保存",
                $"已写回 {edits.Count} 个字节。\n备份文件：{Path.GetFileName(backup)}", "好");
        }
        catch (Exception ex)
        {
            AppLog.Error("hex-save", ex);
            await this.SafeAlertAsync("保存失败", ex.Message, "好");
        }
    }

    /// <summary>WebView 返回的是 JSON 编码字符串，不同平台有的带外层引号；统一解一次。</summary>
    private static string UnwrapScriptResult(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        try
        {
            var wrapped = raw[0] == '"' && raw[^1] == '"' ? raw : "\"" + raw + "\"";
            return JsonSerializer.Deserialize<string>(wrapped) ?? raw;
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    // ---- HTML / JS 生成 ----

    private static string BuildHexHtml(byte[] data, bool dark)
    {
        var css = dark
            ? ":root{--bg:#07090F;--fg:#E9ECF5;--panel:#121828;--line:#222838;--addr:#7C5CFF;--byte:#9CC3F0;--asc:#6B7280;--mod:#3a2a12;--mod-fg:#FFD479;--hit:#C9A227;--hit-fg:#000}"
            : ":root{--bg:#F7F8FC;--fg:#14161F;--panel:#FFFFFF;--line:#E2E6EF;--addr:#7C5CFF;--byte:#1D4ED8;--asc:#6B7280;--mod:#FBE9C7;--mod-fg:#8a5a00;--hit:#C9A227;--hit-fg:#000}";

        var body = new StringBuilder();
        const int bytesPerRow = 16;
        for (var i = 0; i < data.Length; i += bytesPerRow)
        {
            body.Append("<div class=row><span class=addr>").Append(i.ToString("X8")).Append("</span> ");
            var rowBytes = Math.Min(bytesPerRow, data.Length - i);
            var asc = new StringBuilder();
            for (var j = 0; j < rowBytes; j++)
            {
                var b = data[i + j];
                body.Append("<i o=").Append(i + j).Append('>').Append(b.ToString("X2")).Append("</i>");
                asc.Append(b is >= 32 and <= 126 ? (char)b : '.');
            }
            body.Append(" <span class=asc>").Append(WebUtility.HtmlEncode(asc.ToString())).Append("</span></div>");
        }

        return $$"""
<!DOCTYPE html>
<html><head><meta charset=utf-8>
<meta name=viewport content="width=device-width,initial-scale=1">
<style>
  {{css}}
  html,body{margin:0;padding:0;background:var(--bg);color:var(--fg)}
  body{font:13px/1.55 ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;padding:8px 0 56px}
  .row{white-space:nowrap;padding:0 10px}
  .addr{color:var(--addr);margin-right:8px}
  i[o]{color:var(--byte);margin:0 1px}
  i[o].edit{cursor:pointer}
  i[o].edit:active{background:var(--line)}
  i[o].modified{background:var(--mod);color:var(--mod-fg);border-radius:2px}
  .asc{color:var(--asc);margin-left:10px}
  .hit{background:var(--hit);color:var(--hit-fg);border-radius:2px}
  .hit-current{outline:2px solid #FFD400;outline-offset:0}
  #footer{position:fixed;left:0;right:0;bottom:0;padding:8px 12px;background:var(--panel);color:var(--fg);border-top:1px solid var(--line);font:12px ui-monospace,monospace}
  #kbd{position:fixed;inset:0;background:rgba(0,0,0,.55);z-index:50;display:none;align-items:center;justify-content:center}
  #kbd .box{background:var(--panel);border:1px solid var(--line);padding:14px;border-radius:12px;min-width:240px}
  #readout{text-align:center;font:18px ui-monospace,monospace;color:var(--fg);margin-bottom:10px;letter-spacing:2px}
  #keys{display:grid;grid-template-columns:repeat(3,1fr);gap:6px}
  .key{padding:12px 0;font:15px ui-monospace,monospace;background:var(--bg);color:var(--fg);border:1px solid var(--line);border-radius:8px}
  .key.ok{background:var(--addr);color:#fff;border-color:var(--addr)}
  .key.x{background:#2a0e13;color:#F59E9E;border-color:#2a0e13}
</style></head><body>
{{body}}
<div id=footer></div>
<div id=kbd><div class=box><div id=readout></div><div id=keys></div></div></div>
<script>
(function(){
  var editable=false, currentOff=-1, entry="", lastPat=[];
  var matches=[], matchIdx=-1, edits={};
  window.__hex={
    setEditable:function(v){ editable=v;
      var bs=document.querySelectorAll('i[o]');
      for(var i=0;i<bs.length;i++){ bs[i].classList.toggle('edit',v); }
      if(!v){ closeKbd(); }
      updateFooter();
    },
    doSearch:function(s){ clearHits(); matches=[]; matchIdx=-1;
      var pat=parseHex(s);
      if(!pat||!pat.length){ updateMatch(); return; }
      lastPat=pat;
      var n=document.querySelectorAll('i[o]').length;
      for(var i=0;i+pat.length<=n;i++){ var ok=true;
        for(var j=0;j<pat.length;j++){ if(byteVal(i+j)!==pat[j]){ ok=false; break; } }
        if(ok){ matches.push(i);
          for(var j=0;j<pat.length;j++){ var el=byOff(i+j); if(el) el.classList.add('hit'); } } }
      if(matches.length){ matchIdx=0; markCurrent(); }
      updateMatch();
    },
    next:function(){ if(!matches.length) return; matchIdx=(matchIdx+1)%matches.length; markCurrent(); },
    prev:function(){ if(!matches.length) return; matchIdx=(matchIdx-1+matches.length)%matches.length; markCurrent(); },
    getEdits:function(){ return JSON.stringify(edits); },
    afterSave:function(){ edits={}; var ms=document.querySelectorAll('.modified');
      for(var i=0;i<ms.length;i++){ ms[i].classList.remove('modified'); } updateFooter(); }
  };
  function byOff(o){ return document.querySelector('i[o="'+o+'"]'); }
  function byteVal(o){ var el=byOff(o); return el?parseInt(el.textContent,16):-1; }
  function parseHex(s){ var out=[]; var toks=(''+s).trim().split(/\s+/);
    for(var k=0;k<toks.length;k++){ var t=toks[k];
      if(!/^[0-9A-Fa-f]{1,2}$/.test(t)) return null; out.push(parseInt(t,16)); }
    return out; }
  function updateFooter(){ var n=Object.keys(edits).length;
    document.getElementById('footer').textContent = editable ? ('已修改 '+n+' 个字节') : ''; }
  function clearHits(){ var hs=document.querySelectorAll('.hit,.hit-current');
    for(var i=0;i<hs.length;i++){ hs[i].classList.remove('hit'); hs[i].classList.remove('hit-current'); } }
  function markCurrent(){ var hs=document.querySelectorAll('.hit-current');
    for(var i=0;i<hs.length;i++){ hs[i].classList.remove('hit-current'); }
    if(matchIdx<0||!lastPat.length) return;
    var s=matches[matchIdx];
    for(var j=0;j<lastPat.length;j++){ var el=byOff(s+j); if(el) el.classList.add('hit-current'); }
    var first=byOff(s); if(first) first.scrollIntoView({block:'center'}); }
  function updateMatch(){ /* 匹配信息由 C# 通过 getMatchInfo 读取，这里仅占位 */ }
  function openKbd(o){ if(!editable) return; currentOff=o; entry=""; refresh();
    document.getElementById('kbd').style.display='flex';
    var el=byOff(o); if(el) el.scrollIntoView({block:'center'}); }
  function closeKbd(){ document.getElementById('kbd').style.display='none'; currentOff=-1; entry=""; }
  function refresh(){ var base=currentOff>=0?byteHex(currentOff):'00'; var e=(entry+'__').slice(0,2);
    document.getElementById('readout').textContent=base+' -> '+e; }
  function byteHex(o){ var el=byOff(o); return el?el.textContent:'00'; }
  function applyEdit(){ if(currentOff<0) return; if(entry.length===0){ closeKbd(); return; }
    var v=entry; if(v.length===1) v='0'+v; v=v.toUpperCase();
    var el=byOff(currentOff); if(el){ el.textContent=v; el.classList.add('modified'); }
    edits[currentOff]=v; updateFooter(); closeKbd(); }
  function tapKey(c){ if(currentOff<0) return;
    if(c==='OK'){ applyEdit(); return; }
    if(c==='X'){ entry=entry.slice(0,-1); }
    else { if(entry.length<2) entry+=c; }
    refresh(); }
  var grid=document.getElementById('keys');
  var chars=['0','1','2','3','4','5','6','7','8','9','A','B','C','D','E','F','X','OK'];
  for(var i=0;i<chars.length;i++){ (function(ch){
    var b=document.createElement('button'); b.textContent=ch;
    b.className='key'+(ch==='OK'?' ok':'')+(ch==='X'?' x':'');
    b.onclick=function(){ tapKey(ch); }; grid.appendChild(b);
  })(chars[i]); }
  document.addEventListener('click',function(e){
    var t=e.target.closest('i[o]'); if(t&&editable){ openKbd(parseInt(t.getAttribute('o'),10)); } });
  document.getElementById('kbd').addEventListener('click',function(e){ if(e.target===this) closeKbd(); });
})();
</script>
</body></html>
""";
    }

    private static string BuildErrorHtml(string message)
    {
        var escaped = WebUtility.HtmlEncode(message);
        return $$"""
<!DOCTYPE html><html><head><meta charset=utf-8></head><body style="background:#07090F;color:#F59E9E;font:14px sans-serif;padding:20px">
无法加载文件：<br>{{escaped}}
</body></html>
""";
    }
}
