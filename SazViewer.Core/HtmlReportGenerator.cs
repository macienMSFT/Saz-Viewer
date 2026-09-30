using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace SazViewer.Core;

public sealed class HtmlReportGenerator
{
    private readonly BodyFormatter bodyFormatter = new();

    public string Generate(SazReport report)
    {
        var html = new StringBuilder(256 * 1024);
        html.Append("""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src data:">
<title>SAZ capture</title>
<style>
:root{color-scheme:light dark;--bg:#0d1117;--panel:#161b22;--panel2:#21262d;--text:#e6edf3;--muted:#8b949e;--line:#30363d;--accent:#58a6ff;--warn:#d29922;--selected:#1f6feb55;--detail-height:38vh}
*{box-sizing:border-box}html{scrollbar-gutter:stable}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 system-ui,Segoe UI,sans-serif}
main{width:100%;padding:4px}h2,h3,h4{margin:.25em 0}.muted,.format-status{color:var(--muted)}
.controls{display:flex;gap:6px;flex-wrap:wrap;margin:0 0 4px}input,select,button{background:var(--panel);border:1px solid var(--line);border-radius:6px;color:var(--text);padding:7px 10px}
input{min-width:280px;flex:1}button{cursor:pointer}
table{width:100%;border-collapse:collapse;background:var(--panel);font-size:13px}th{position:sticky;top:0;z-index:2;background:var(--panel2);text-align:left}
th,td{padding:8px;border:1px solid var(--line);vertical-align:top}tbody tr:hover{background:#1f2630}#httpTable tbody tr{cursor:pointer}
#httpTable tbody tr:focus{outline:2px solid var(--accent);outline-offset:-2px}#httpTable tbody tr.selected{background:var(--selected);box-shadow:inset 4px 0 var(--accent)}
.url{max-width:560px;word-break:break-all}.num{text-align:right;white-space:nowrap}.badge,.format-badge{padding:2px 7px;border:1px solid var(--line);border-radius:10px;white-space:nowrap}
details{margin:4px 0}summary{cursor:pointer;color:var(--accent)}pre{white-space:pre-wrap;overflow:auto;background:var(--bg);border:1px solid var(--line);padding:10px;word-break:break-word;tab-size:2}
.warning{border-left:4px solid var(--warn);padding:6px 10px;margin:5px 0;background:#2b2111}.hidden{display:none!important}
.http-workspace{height:calc(100vh - 8px);height:calc(100dvh - 8px);min-height:420px;display:flex;flex-direction:column}.http-table-scroll{min-height:180px;flex:1;overflow:auto;border:1px solid var(--line)}
.detail-pane{position:sticky;bottom:0;z-index:5;flex:0 0 64px;min-height:64px;background:var(--panel);border:1px solid var(--line);box-shadow:0 -8px 22px #0008;overflow:hidden}
.detail-pane.has-selection{flex-basis:var(--detail-height)}.detail-resizer{height:9px;cursor:row-resize;touch-action:none;background:linear-gradient(transparent 3px,var(--line) 3px,var(--line) 5px,transparent 5px)}
.detail-resizer:focus{outline:2px solid var(--accent);outline-offset:-2px}.detail-content{height:calc(100% - 9px);overflow:auto;padding:10px 14px}.detail-placeholder{display:flex;height:100%;align-items:center;justify-content:center;color:var(--muted)}
.session-heading{display:flex;gap:10px;align-items:baseline;flex-wrap:wrap;margin-bottom:8px}.session-heading .url{font-weight:600}
.message-grid{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr);gap:12px}.message-panel{display:flex;flex-direction:column;min-width:0;border:1px solid var(--line);border-radius:7px;padding:10px;background:var(--bg)}
.headers{white-space:pre-wrap}
.tab-strip{display:flex;gap:2px;flex-wrap:wrap;border-bottom:1px solid var(--line);margin:8px 0 0}
.tab-strip [role=tab]{background:transparent;border:1px solid transparent;border-bottom:none;border-radius:6px 6px 0 0;padding:6px 12px;color:var(--muted);cursor:pointer;font:inherit}
.tab-strip [role=tab][aria-selected=true]{color:var(--text);background:var(--panel);border-color:var(--line);border-bottom:2px solid var(--accent);margin-bottom:-1px}
.tab-strip [role=tab]:disabled{color:#4b535c;cursor:not-allowed;opacity:.5}
.tab-strip [role=tab]:focus-visible{outline:2px solid var(--accent);outline-offset:-2px;z-index:1}
.tab-panels{flex:1;min-height:260px;max-height:58vh;overflow:auto;border:1px solid var(--line);border-top:none;background:var(--panel);padding:10px 12px;margin-bottom:2px}
.tab-panel.hidden{display:none!important}.tab-empty{color:var(--muted);padding:14px 4px}
.format-meta{display:flex;gap:8px;align-items:center;flex-wrap:wrap;margin:0 0 6px}.format-meta .format-status{flex:1;min-width:180px}
.captured-bytes{margin-top:8px}.captured-bytes summary{cursor:pointer;color:var(--accent)}
.body-view{margin:6px 0}.decode-status{margin:6px 0;padding:6px 8px;border-left:3px solid var(--accent);background:#13233a}.session-meta{margin-top:10px}.session-meta pre{max-height:180px}.empty-message{color:var(--muted);padding:18px;text-align:center}
.protocol-meta{margin-bottom:6px}.protocol-block{margin-top:6px}.protocol-toolbar{display:flex;gap:6px;align-items:center;flex-wrap:wrap;margin:5px 0}.protocol-toolbar input{min-width:160px}.protocol-tree{overflow:visible;border:1px solid var(--line);padding:6px;background:var(--panel)}.protocol-node{margin-left:14px}.protocol-node>summary{display:flex;gap:7px;align-items:baseline}.protocol-field{display:flex;gap:7px;margin-left:16px;padding:2px 0}.protocol-offset{color:var(--muted);font:12px ui-monospace,Consolas,monospace}.protocol-value{font-family:ui-monospace,Consolas,monospace;overflow-wrap:anywhere}.protocol-kind{color:var(--accent);font-size:12px}.protocol-hidden{display:none!important}
.syn-key{color:#79c0ff}.syn-string{color:#a5d6ff}.syn-number{color:#ffa657}.syn-literal{color:#ff7b72}.syn-punct{color:#8b949e}.syn-tag{color:#7ee787}.syn-attr{color:#d2a8ff}.syn-comment{color:#8b949e;font-style:italic}.syn-value{color:#a5d6ff}
.ws-table-scroll{max-height:70vh;overflow:auto;border:1px solid var(--line);margin-bottom:24px}.ws-table-scroll pre{max-height:320px}
@media(max-width:900px){main{padding:2px}.message-grid{grid-template-columns:1fr}.detail-pane.has-selection{--detail-height:52vh}.tab-panels{max-height:44vh}.http-workspace{height:calc(100vh - 4px);height:calc(100dvh - 4px);min-height:360px}}
</style>
</head>
<body><main>
""");
        AppendHttpSection(html, report.Sessions);
        AppendWarnings(html, report.Warnings);
        AppendWebSocketSection(html, report.WebSocketMessages);
        html.Append("""
</main>
<script>
(()=>{
const httpRows=[...document.querySelectorAll('#httpTable tbody tr')];
const detailPane=document.getElementById('httpDetails');
const detailContent=document.getElementById('httpDetailContent');
let selectedRow=null;
function showPlaceholder(message){
  detailContent.replaceChildren();
  const placeholder=document.createElement('div');
  placeholder.className='detail-placeholder';
  placeholder.textContent=message;
  detailContent.append(placeholder);
}
function renderProtocolTrees(root){
  root.querySelectorAll('[data-protocol]').forEach(host=>{
    let data;
    try{data=JSON.parse(host.dataset.protocol)}
    catch{host.textContent='Protocol tree data could not be loaded.';host.className='warning';return}
    host.removeAttribute('data-protocol');
    const toolbar=document.createElement('div');toolbar.className='protocol-toolbar';
    const search=document.createElement('input');search.type='search';search.placeholder='Search protocol fields...';search.setAttribute('aria-label','Search protocol tree');
    const expand=document.createElement('button');expand.type='button';expand.textContent='Expand all';
    const collapse=document.createElement('button');collapse.type='button';collapse.textContent='Collapse all';
    toolbar.append(search,expand,collapse);
    const tree=document.createElement('div');tree.className='protocol-tree';tree.setAttribute('role','tree');
    function addNode(node,parent){
      const hasChildren=Array.isArray(node.children)&&node.children.length>0;
      const element=document.createElement(hasChildren?'details':'div');
      element.className=hasChildren?'protocol-node':'protocol-field';
      const line=document.createElement(hasChildren?'summary':'span');
      const name=document.createElement('b');name.textContent=node.name;line.append(name);
      const kind=document.createElement('span');kind.className='protocol-kind';kind.textContent=node.kind;line.append(kind);
      const offset=document.createElement('span');offset.className='protocol-offset';offset.textContent=`@${node.offset} +${node.length}`;line.append(offset);
      if(node.value!==null&&node.value!==undefined){const value=document.createElement('span');value.className='protocol-value';value.textContent=String(node.value);line.append(value)}
      element.append(line);element.dataset.search=line.textContent.toLowerCase();
      if(hasChildren)node.children.forEach(child=>addNode(child,element));
      parent.append(element);
    }
    addNode(data.root,tree);host.replaceChildren(toolbar,tree);
    function filterNode(node,query){
      const children=[...node.children].filter(child=>child.classList.contains('protocol-node')||child.classList.contains('protocol-field'));
      let childMatch=false;children.forEach(child=>{if(filterNode(child,query))childMatch=true});
      const ownMatch=!query||(node.dataset.search||'').includes(query);
      const visible=ownMatch||childMatch;node.classList.toggle('protocol-hidden',!visible);
      if(query&&childMatch&&node.tagName==='DETAILS')node.open=true;
      return visible;
    }
    search.addEventListener('input',()=>filterNode(tree.firstElementChild,search.value.trim().toLowerCase()));
    expand.addEventListener('click',()=>tree.querySelectorAll('details').forEach(item=>item.open=true));
    collapse.addEventListener('click',()=>tree.querySelectorAll('details').forEach(item=>item.open=false));
  });
}
function clearSelection(message){
  if(selectedRow){selectedRow.classList.remove('selected');selectedRow.setAttribute('aria-selected','false')}
  selectedRow=null;detailPane.classList.remove('has-selection');
  resizer?.setAttribute('aria-disabled','true');resizer?.setAttribute('aria-valuenow','180');
  showPlaceholder(message||'Select an HTTP session to inspect its request and response.');
}
function selectRow(row){
  const template=document.getElementById(row.dataset.detail);
  if(!template)return;
  if(selectedRow){selectedRow.classList.remove('selected');selectedRow.setAttribute('aria-selected','false')}
  selectedRow=row;row.classList.add('selected');row.setAttribute('aria-selected','true');
  detailContent.replaceChildren(template.content.cloneNode(true));detailPane.classList.add('has-selection');
  highlightSelected(detailContent);renderProtocolTrees(detailContent);setupTabs(detailContent);resizer.setAttribute('aria-disabled','false');
  requestAnimationFrame(updateResizeAria);
}
httpRows.forEach(row=>{
  row.addEventListener('click',()=>selectRow(row));
  row.addEventListener('keydown',event=>{
    if(event.key==='Enter'||event.key===' '){event.preventDefault();selectRow(row)}
  });
});
function bindFilter(inputId,selectId,tableId,onFiltered){
  const input=document.getElementById(inputId),select=document.getElementById(selectId),rows=document.querySelectorAll(`#${tableId} tbody tr`);
  function apply(){
    const query=input.value.toLowerCase(),filter=select.value;
    rows.forEach(row=>{
      const filterMatch=!filter||(filter==='mapi'?row.dataset.mapi==='true':row.dataset.filter===filter);
      const visible=(!query||row.dataset.search.includes(query))&&filterMatch;
      row.classList.toggle('hidden',!visible);
    });
    if(onFiltered)onFiltered();
  }
  input.addEventListener('input',apply);select.addEventListener('change',apply);
}
bindFilter('httpSearch','httpFilter','httpTable',()=>{
  if(selectedRow&&selectedRow.classList.contains('hidden')){
    clearSelection('The selected session is hidden by the active filter. Select a visible session to inspect it.');
  }
});
bindFilter('wsSearch','wsFilter','wsTable');
const preferredTab={};
function tabsOf(tablist){return [...tablist.querySelectorAll('[role="tab"]')]}
function activateTab(tablist,key,options){
  const tabs=tabsOf(tablist),target=tabs.find(tab=>tab.dataset.tab===key&&!tab.disabled);
  if(!target)return;
  const panels=tablist.parentElement.querySelectorAll(':scope>.tab-panels>.tab-panel');
  tabs.forEach(tab=>{const active=tab===target;tab.setAttribute('aria-selected',String(active));tab.tabIndex=active?0:-1});
  panels.forEach(panel=>panel.classList.toggle('hidden',panel.id!==target.getAttribute('aria-controls')));
  if(options&&options.remember)preferredTab[tablist.dataset.side]=key;
  if(options&&options.focus)target.focus();
}
function initialTabFor(tablist){
  const tabs=tabsOf(tablist);
  const enabled=key=>tabs.some(tab=>tab.dataset.tab===key&&!tab.disabled);
  const remembered=preferredTab[tablist.dataset.side];
  if(remembered&&enabled(remembered))return remembered;
  for(const key of ['mapi','json','xml','raw','headers']){if(enabled(key))return key}
  return null;
}
function setupTabList(tablist){
  const key=initialTabFor(tablist);
  if(key)activateTab(tablist,key);
  tablist.addEventListener('click',event=>{
    const tab=event.target.closest('[role="tab"]');
    if(!tab||tab.disabled||tab.parentElement!==tablist)return;
    activateTab(tablist,tab.dataset.tab,{remember:true});
  });
  tablist.addEventListener('keydown',event=>{
    if(!['ArrowLeft','ArrowRight','Home','End'].includes(event.key))return;
    const enabledTabs=tabsOf(tablist).filter(tab=>!tab.disabled);
    if(enabledTabs.length===0)return;
    const currentIndex=Math.max(0,enabledTabs.indexOf(document.activeElement));
    let nextIndex=currentIndex;
    if(event.key==='ArrowRight')nextIndex=(currentIndex+1)%enabledTabs.length;
    else if(event.key==='ArrowLeft')nextIndex=(currentIndex-1+enabledTabs.length)%enabledTabs.length;
    else if(event.key==='Home')nextIndex=0;
    else if(event.key==='End')nextIndex=enabledTabs.length-1;
    event.preventDefault();
    activateTab(tablist,enabledTabs[nextIndex].dataset.tab,{focus:true,remember:true});
  });
}
function setupTabs(root){root.querySelectorAll('[role="tablist"]').forEach(setupTabList)}
const resizer=document.getElementById('detailResizer');
function appendSpan(fragment,className,text){
  const span=document.createElement('span');span.className=className;span.textContent=text;fragment.append(span);
}
function appendText(fragment,text){fragment.append(document.createTextNode(text))}
function highlightJson(pre){
  const value=pre.textContent,fragment=document.createDocumentFragment();let index=0;
  while(index<value.length){
    const start=index,character=value[index];
    if(character==='"'){
      index++;let escaped=false;
      while(index<value.length){
        const current=value[index++];
        if(current==='"'&&!escaped)break;
        escaped=current==='\\'&&!escaped;if(current!=='\\')escaped=false;
      }
      let lookahead=index;while(lookahead<value.length&&/\s/.test(value[lookahead]))lookahead++;
      appendSpan(fragment,lookahead<value.length&&value[lookahead]===':'?'syn-key':'syn-string',value.slice(start,index));
    }else if(/[0-9-]/.test(character)){
      index++;while(index<value.length&&/[0-9.eE+-]/.test(value[index]))index++;
      appendSpan(fragment,'syn-number',value.slice(start,index));
    }else if(/[A-Za-z]/.test(character)){
      index++;while(index<value.length&&/[A-Za-z]/.test(value[index]))index++;
      appendSpan(fragment,'syn-literal',value.slice(start,index));
    }else if('{}[]:,'.includes(character)){
      appendSpan(fragment,'syn-punct',character);index++;
    }else{appendText(fragment,character);index++}
  }
  pre.replaceChildren(fragment);
}
function highlightXml(pre){
  const value=pre.textContent,fragment=document.createDocumentFragment();let index=0;
  while(index<value.length){
    if(value[index]!=='<'){
      const next=value.indexOf('<',index),end=next<0?value.length:next;
      appendText(fragment,value.slice(index,end));index=end;continue;
    }
    if(value.startsWith('<!--',index)||value.startsWith('<![CDATA[',index)){
      const terminator=value.startsWith('<!--',index)?'-->':']]>',end=value.indexOf(terminator,index);
      const stop=end<0?value.length:end+terminator.length;
      appendSpan(fragment,'syn-comment',value.slice(index,stop));index=stop;continue;
    }
    let end=index+1,quote='';
    while(end<value.length){
      const current=value[end++];
      if(quote){if(current===quote)quote=''}
      else if(current==='"'||current==="'")quote=current;
      else if(current==='>')break;
    }
    appendSpan(fragment,'syn-tag',value.slice(index,end));index=end;
  }
  pre.replaceChildren(fragment);
}
function highlightSelected(root){
  root.querySelectorAll('.formatted-view[data-format]').forEach(pre=>{
    if(pre.dataset.format==='json')highlightJson(pre);
    else if(pre.dataset.format==='xml')highlightXml(pre);
  });
}
function updateResizeAria(){
  const maximum=Math.max(180,Math.round(window.innerHeight*.75));
  const current=detailPane.classList.contains('has-selection')?Math.round(detailPane.getBoundingClientRect().height):180;
  resizer.setAttribute('aria-valuemax',String(maximum));resizer.setAttribute('aria-valuenow',String(current));
}
function resizeTo(height){
  const limited=Math.max(180,Math.min(Math.max(180,window.innerHeight*.75),height));
  detailPane.style.setProperty('--detail-height',`${limited}px`);updateResizeAria();
}
resizer.addEventListener('pointerdown',event=>{
  if(!detailPane.classList.contains('has-selection'))return;
  const startY=event.clientY,startHeight=detailPane.getBoundingClientRect().height;
  resizer.setPointerCapture(event.pointerId);
  const move=moveEvent=>resizeTo(startHeight+startY-moveEvent.clientY);
  const stop=()=>{resizer.removeEventListener('pointermove',move);resizer.removeEventListener('pointerup',stop);resizer.removeEventListener('pointercancel',stop)};
  resizer.addEventListener('pointermove',move);resizer.addEventListener('pointerup',stop);resizer.addEventListener('pointercancel',stop);
});
resizer.addEventListener('keydown',event=>{
  if(!detailPane.classList.contains('has-selection')||!['ArrowUp','ArrowDown'].includes(event.key))return;
  event.preventDefault();const delta=event.key==='ArrowUp'?24:-24;resizeTo(detailPane.getBoundingClientRect().height+delta);
});
window.addEventListener('resize',()=>{
  if(detailPane.classList.contains('has-selection'))resizeTo(detailPane.getBoundingClientRect().height);
  else updateResizeAria();
});
clearSelection();
})();
</script>
</body></html>
""");
        return html.ToString();
    }

    private static void AppendWarnings(StringBuilder html, IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0)
        {
            return;
        }

        html.Append("<section><h2>Warnings</h2><details");
        if (warnings.Count <= 5)
        {
            html.Append(" open");
        }
        html.Append("><summary>").Append(warnings.Count).Append(" warning(s)</summary>");
        foreach (var warning in warnings.Take(500))
        {
            html.Append("<div class=\"warning\">");
            Text(html, warning);
            html.Append("</div>");
        }
        if (warnings.Count > 500)
        {
            html.Append("<div class=\"warning\">Additional warnings omitted from display.</div>");
        }
        html.Append("</details></section>");
    }

    private void AppendHttpSection(StringBuilder html, IReadOnlyList<HttpSession> sessions)
    {
        html.Append("""
<section class="http-workspace" aria-label="HTTP sessions">
<div class="controls"><input id="httpSearch" type="search" aria-label="Search HTTP sessions" placeholder="Search method, URL, status, content type, endpoints...">
<select id="httpFilter" aria-label="Filter HTTP status or protocol"><option value="">All sessions</option><option value="mapi">MAPI/NSPI only</option><option value="2">2xx</option><option value="3">3xx</option><option value="4">4xx</option><option value="5">5xx</option><option value="0">Missing/other</option></select></div>
<div class="http-table-scroll"><table id="httpTable"><thead><tr><th>Time</th><th>ID</th><th>Method</th><th>Protocol</th><th>URL</th><th>Status</th><th>Type</th><th class="num">Req</th><th class="num">Resp</th></tr></thead><tbody>
""");
        for (var index = 0; index < sessions.Count; index++)
        {
            AppendHttpRow(html, sessions[index], index);
        }
        html.Append("""
</tbody></table></div>
<aside id="httpDetails" class="detail-pane" aria-label="Selected HTTP session details">
<div id="detailResizer" class="detail-resizer" role="separator" aria-label="Resize HTTP detail pane" aria-orientation="horizontal" aria-valuemin="180" aria-valuemax="900" aria-valuenow="320" aria-disabled="true" tabindex="0"></div>
<div id="httpDetailContent" class="detail-content" aria-live="polite"></div>
</aside>
<div class="session-templates" hidden>
""");
        for (var index = 0; index < sessions.Count; index++)
        {
            AppendSessionTemplate(html, sessions[index], index);
        }
        html.Append("</div></section>");
    }

    private static void AppendHttpRow(StringBuilder html, HttpSession session, int index)
    {
        var filter = session.StatusCode is >= 200 and <= 599
            ? (session.StatusCode.Value / 100).ToString(CultureInfo.InvariantCulture)
            : "0";
        var search = string.Join(' ', new[]
        {
            session.Id, session.Method, session.Url, session.StatusCode?.ToString(CultureInfo.InvariantCulture),
            session.StatusText, session.ContentType, session.ClientEndpoint, session.ServerEndpoint,
            session.Mapi?.RequestType, session.Mapi?.Endpoint.ToString()
        }.Where(value => !string.IsNullOrWhiteSpace(value))).ToLowerInvariant();
        html.Append("<tr tabindex=\"0\" aria-selected=\"false\" aria-label=\"Inspect HTTP session ");
        Attribute(html, session.Id);
        html.Append("\" data-detail=\"http-detail-").Append(index).Append("\" data-filter=\"")
            .Append(filter).Append("\" data-mapi=\"").Append(session.Mapi is not null ? "true" : "false")
            .Append("\" data-search=\"");
        Attribute(html, search);
        html.Append("\"><td>");
        Text(html, FormatTimestamp(session.Timestamp));
        html.Append("</td><td>");
        Text(html, session.Id);
        html.Append("</td><td><span class=\"badge\">");
        Text(html, session.Method ?? "-");
        html.Append("</span></td><td class=\"url\">");
        if (session.Mapi is not null)
        {
            html.Append("<span class=\"badge\">");
            Text(html, session.Mapi.Endpoint == MapiEndpoint.AddressBook ? "NSPI" : "MAPI");
            html.Append("</span>");
        }
        else
        {
            html.Append("-");
        }
        html.Append("</td><td class=\"url\">");
        Text(html, session.Url ?? "-");
        html.Append("</td><td>");
        Text(html, session.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "-");
        if (!string.IsNullOrWhiteSpace(session.StatusText))
        {
            html.Append(' ');
            Text(html, session.StatusText);
        }
        html.Append("</td><td>");
        Text(html, session.ContentType ?? "-");
        html.Append("</td><td class=\"num\">").Append(FormatBytes(session.RequestBytes))
            .Append("</td><td class=\"num\">").Append(FormatBytes(session.ResponseBytes))
            .Append("</td></tr>");
    }

    private void AppendSessionTemplate(StringBuilder html, HttpSession session, int index)
    {
        html.Append("<template id=\"http-detail-").Append(index).Append("\"><div class=\"session-heading\"><h3>Session ");
        Text(html, session.Id);
        html.Append("</h3><span class=\"badge\">");
        Text(html, session.Method ?? "-");
        html.Append("</span><span class=\"url\">");
        Text(html, session.Url ?? "-");
        html.Append("</span></div><div class=\"message-grid\">");
        AppendMessagePanel(html, "Request", "request", session.Request, session.Mapi?.Request);
        AppendMessagePanel(html, "Response", "response", session.Response, session.Mapi?.Response);
        html.Append("</div><div class=\"session-meta\">");
        if (session.ClientEndpoint is not null || session.ServerEndpoint is not null)
        {
            html.Append("<p><b>Endpoints:</b> ");
            Text(html, $"{session.ClientEndpoint ?? "?"} -> {session.ServerEndpoint ?? "?"}");
            html.Append("</p>");
        }
        if (session.Timers.Count > 0)
        {
            html.Append("<details><summary>Timers</summary><pre>");
            foreach (var timer in session.Timers)
            {
                Text(html, $"{timer.Key}: {timer.Value}\n");
            }
            html.Append("</pre></details>");
        }
        foreach (var warning in session.Warnings)
        {
            html.Append("<div class=\"warning\">");
            Text(html, warning);
            html.Append("</div>");
        }
        html.Append("</div></template>");
    }

    private void AppendMessagePanel(
        StringBuilder html,
        string title,
        string side,
        HttpMessage? message,
        MapiMessageParse? protocol)
    {
        html.Append("<section class=\"message-panel\"><h3>").Append(title).Append("</h3>");

        var lowerTitle = title.ToLowerInvariant();
        var body = message is null ? null : bodyFormatter.Format(message.Body, message.Header("Content-Type"));
        var jsonEnabled = body is { Format: BodyFormat.Json, CanToggle: true };
        var xmlEnabled = body is { Format: BodyFormat.Xml, CanToggle: true };
        var mapiEnabled = protocol is not null;
        var headersEnabled = message is not null && message.Headers.Count > 0;
        var rawEnabled = message is not null;
        var anyEnabled = jsonEnabled || xmlEnabled || mapiEnabled || headersEnabled || rawEnabled;

        string? initial = !anyEnabled
            ? null
            : mapiEnabled
                ? "mapi"
                : jsonEnabled
                    ? "json"
                    : xmlEnabled
                        ? "xml"
                        : rawEnabled
                            ? "raw"
                            : "headers";

        AppendTabStrip(html, title, side, jsonEnabled, xmlEnabled, mapiEnabled, headersEnabled, rawEnabled, initial);

        html.Append("<div class=\"tab-panels\">");
        AppendTabPanel(
            html, side, "json", initial == "json", jsonEnabled,
            $"JSON view is not available: the {lowerTitle} body is not recognized, valid JSON.",
            jsonEnabled ? inner => AppendFormattedBody(inner, body!, "json") : null);
        AppendTabPanel(
            html, side, "xml", initial == "xml", xmlEnabled,
            $"XML view is not available: the {lowerTitle} body is not recognized, valid XML.",
            xmlEnabled ? inner => AppendFormattedBody(inner, body!, "xml") : null);
        AppendTabPanel(
            html, side, "mapi", initial == "mapi", mapiEnabled,
            $"MAPI view is not available: no protocol tree was parsed for this {lowerTitle}.",
            mapiEnabled ? inner => AppendProtocol(inner, protocol!) : null);
        AppendTabPanel(
            html, side, "headers", initial == "headers", headersEnabled,
            message is null
                ? $"No {lowerTitle} entry was captured."
                : $"No headers were captured for this {lowerTitle}.",
            headersEnabled ? inner => AppendHeadersOnly(inner, message!) : null);
        AppendTabPanel(
            html, side, "raw", initial == "raw", rawEnabled,
            $"No {lowerTitle} entry was captured.",
            rawEnabled ? inner => AppendRawView(inner, message!, body!) : null);
        if (!anyEnabled)
        {
            html.Append("<div class=\"tab-empty\">No ").Append(lowerTitle).Append(" entry was captured.</div>");
        }
        html.Append("</div></section>");
    }

    private static void AppendTabStrip(
        StringBuilder html,
        string title,
        string side,
        bool jsonEnabled,
        bool xmlEnabled,
        bool mapiEnabled,
        bool headersEnabled,
        bool rawEnabled,
        string? initial)
    {
        html.Append("<div class=\"tab-strip\" role=\"tablist\" aria-label=\"").Append(title)
            .Append(" detail views\" data-side=\"").Append(side).Append("\">");
        AppendTabButton(html, side, "json", "JSON", jsonEnabled, initial == "json");
        AppendTabButton(html, side, "xml", "XML", xmlEnabled, initial == "xml");
        AppendTabButton(html, side, "mapi", "MAPI", mapiEnabled, initial == "mapi");
        AppendTabButton(html, side, "headers", "Headers", headersEnabled, initial == "headers");
        AppendTabButton(html, side, "raw", "Raw", rawEnabled, initial == "raw");
        html.Append("</div>");
    }

    private static void AppendTabButton(
        StringBuilder html,
        string side,
        string key,
        string label,
        bool enabled,
        bool selected)
    {
        html.Append("<button type=\"button\" role=\"tab\" id=\"").Append(side).Append("-tab-").Append(key)
            .Append("\" aria-controls=\"").Append(side).Append("-panel-").Append(key)
            .Append("\" aria-selected=\"").Append(selected ? "true" : "false")
            .Append("\" data-tab=\"").Append(key)
            .Append("\" tabindex=\"").Append(selected ? "0" : "-1").Append('"');
        if (!enabled)
        {
            html.Append(" disabled aria-disabled=\"true\"");
        }
        html.Append('>').Append(label).Append("</button>");
    }

    private static void AppendTabPanel(
        StringBuilder html,
        string side,
        string key,
        bool selected,
        bool enabled,
        string unavailableMessage,
        Action<StringBuilder>? content)
    {
        html.Append("<div role=\"tabpanel\" id=\"").Append(side).Append("-panel-").Append(key)
            .Append("\" aria-labelledby=\"").Append(side).Append("-tab-").Append(key)
            .Append("\" tabindex=\"0\" class=\"tab-panel");
        if (!selected)
        {
            html.Append(" hidden");
        }
        html.Append('"').Append('>');
        if (enabled && content is not null)
        {
            content(html);
        }
        else
        {
            html.Append("<div class=\"tab-empty\">");
            Text(html, unavailableMessage);
            html.Append("</div>");
        }
        html.Append("</div>");
    }

    private static void AppendHeadersOnly(StringBuilder html, HttpMessage message)
    {
        html.Append("<pre class=\"headers\">");
        Text(html, message.StartLine + "\n");
        foreach (var header in message.Headers)
        {
            Text(html, $"{header.Name}: {header.Value}\n");
        }
        html.Append("</pre>");
    }

    private static void AppendFormattedBody(StringBuilder html, BodyPresentation body, string format)
    {
        html.Append("<div class=\"format-meta\"><span class=\"format-badge\">");
        Text(html, body.Label);
        html.Append("</span><span class=\"format-status\">");
        Text(html, body.Status);
        html.Append("</span></div><pre class=\"body-view formatted-view\" data-format=\"")
            .Append(format).Append("\">");
        Text(html, body.Formatted);
        html.Append("</pre>");
    }

    private static void AppendRawView(StringBuilder html, HttpMessage message, BodyPresentation body)
    {
        var source = message.Body;
        html.Append("<h4>Original headers</h4>");
        AppendHeadersOnly(html, message);
        html.Append("<h4>").Append(source.WasDecoded ? "Decoded body" : "Body").Append(" <span class=\"muted\">(");
        if (source.WasDecoded)
        {
            html.Append(FormatBytes(source.Length)).Append(" decoded; ")
                .Append(FormatBytes(source.CapturedLength)).Append(" captured");
        }
        else
        {
            html.Append(FormatBytes(source.Length));
        }
        html.Append(")</span></h4><div class=\"format-meta\"><span class=\"format-badge\">");
        Text(html, body.Label);
        html.Append("</span><span class=\"format-status\">");
        Text(html, body.Status);
        html.Append("</span></div>");
        if (source.DecodingStatus is not null)
        {
            html.Append(source.WasDecoded ? "<div class=\"decode-status\">" : "<div class=\"warning\">");
            Text(html, source.DecodingStatus);
            html.Append("</div>");
        }
        html.Append("<pre class=\"body-view\">");
        Text(html, body.Raw);
        html.Append("</pre>");
        if (source.CapturedBytesPreview is not null)
        {
            html.Append("<details class=\"captured-bytes\"><summary>Captured bytes (pre-decode)</summary><pre>");
            Text(html, source.CapturedBytesPreview);
            if (source.CapturedBytesPreviewTruncated)
            {
                Text(html, "\n[Captured byte preview truncated]");
            }
            html.Append("</pre></details>");
        }
    }

    private static void AppendProtocol(StringBuilder html, MapiMessageParse protocol)
    {
        html.Append("<div class=\"protocol-meta muted\">(")
            .Append(protocol.Complete ? "complete" : "partial").Append(", ")
            .Append(protocol.ParsedBytes.ToString("N0", CultureInfo.InvariantCulture)).Append(" of ")
            .Append(protocol.TotalBytes.ToString("N0", CultureInfo.InvariantCulture))
            .Append(" bytes)</div>");
        foreach (var warning in protocol.Warnings.Take(50))
        {
            html.Append("<div class=\"warning\">");
            Text(html, warning);
            html.Append("</div>");
        }
        var payload = JsonSerializer.Serialize(new
        {
            root = ToProtocolData(protocol.Root)
        });
        html.Append("<div class=\"protocol-block\" data-protocol=\"");
        Attribute(html, payload);
        html.Append("\"><span class=\"muted\">Protocol tree loads when this session is selected.</span></div>");
    }

    private static object ToProtocolData(MapiNode node) => new
    {
        name = node.Name,
        kind = node.Kind.ToString(),
        offset = node.Offset,
        length = node.Length,
        value = node.Value,
        children = node.Children.Select(ToProtocolData).ToArray()
    };

    private static void AppendWebSocketSection(StringBuilder html, IReadOnlyList<WebSocketMessage> messages)
    {
        html.Append("""
<section><h2>WebSocket messages</h2>
<div class="controls"><input id="wsSearch" type="search" aria-label="Search WebSocket messages" placeholder="Search WebSocket session, direction, type, preview...">
<select id="wsFilter" aria-label="Filter WebSocket direction"><option value="">All directions</option><option value="Client">Client to server</option><option value="Server">Server to client</option><option value="Unknown">Unknown</option></select></div>
<div class="ws-table-scroll"><table id="wsTable"><thead><tr><th>Time</th><th>Session</th><th>#</th><th>Direction</th><th>Type</th><th class="num">Length</th><th>Preview</th></tr></thead><tbody>
""");
        foreach (var message in messages)
        {
            var search = $"{message.SessionId} {message.Direction} {message.Type} {message.Preview}".ToLowerInvariant();
            html.Append("<tr data-filter=\"");
            Attribute(html, message.Direction);
            html.Append("\" data-search=\"");
            Attribute(html, search);
            html.Append("\"><td>");
            Text(html, FormatTimestamp(message.Timestamp));
            html.Append("</td><td>");
            Text(html, message.SessionId);
            html.Append("</td><td>").Append(message.RecordIndex).Append("</td><td>");
            Text(html, message.Direction);
            html.Append("</td><td>");
            Text(html, message.Type);
            html.Append("</td><td class=\"num\">").Append(FormatBytes(message.PayloadLength))
                .Append("</td><td><pre>");
            Text(html, message.Preview);
            html.Append("</pre>");
            if (message.Warning is not null)
            {
                html.Append("<div class=\"warning\">");
                Text(html, message.Warning);
                html.Append("</div>");
            }
            html.Append("</td></tr>");
        }
        html.Append("</tbody></table></div></section>");
    }

    private static string FormatTimestamp(DateTimeOffset? value) =>
        value?.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture) ?? "-";

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        var size = (double)value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{value.ToString("N0", CultureInfo.InvariantCulture)} {units[unit]}"
            : $"{size.ToString("N1", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private static void Text(StringBuilder html, string? value) =>
        html.Append(WebUtility.HtmlEncode(value ?? string.Empty));

    private static void Attribute(StringBuilder html, string? value) =>
        html.Append(WebUtility.HtmlEncode(value ?? string.Empty));
}
