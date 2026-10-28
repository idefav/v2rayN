'use strict';
// Offline model / generated-markup tests. This fixture is not a browser or a layout engine.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const html = fs.readFileSync(path.join(__dirname, '..', 'index.html'), 'utf8');
const source = html.match(/<script>([\s\S]*?)<\/script>/)[1];
const listeners = new Map();
function on(key,fn){const all=listeners.get(key)||[];all.push(fn);listeners.set(key,all)}
function emit(type,target,extra={}){const event={target,key:'',preventDefault(){this.defaultPrevented=true},...extra};for(const fn of listeners.get('document:'+type)||[])fn(event);return event}
class Element {
  constructor(id) { this.id=id; this.dataset={}; this.attributes={}; this.innerHTML=''; this.textContent=''; this.open=false; this.scrollTop=0; this.offsetTop=0; this.isConnected=true; this.classList={contains:()=>false}; }
  setAttribute(k,v) { this.attributes[k]=v; }
  addEventListener(k,fn) { on(this.id+':'+k,fn); }
  querySelector() { return null; }
  querySelectorAll() { return []; }
  closest() { return this; }
  hasAttribute(k) { return k in this.attributes; }
  appendChild(child) { return child; }
  remove() { this.isConnected=false; }
  focus() { document.activeElement=this; }
  showModal() { this.open=true; }
  close() { this.open=false; }
}
const nodes = new Map();
const document = {
  activeElement:null,
  createElement(tag) { return new Element(tag); },
  documentElement:{lang:'zh-CN',dataset:{}},
  getElementById(id) { if(!nodes.has(id))nodes.set(id,new Element(id)); return nodes.get(id); },
  querySelector() { return null; }, querySelectorAll() { return []; },
  addEventListener(k,fn) { on('document:'+k,fn); }
};
const storage = new Map();
const window = {innerWidth:1440,addEventListener(){}};
const context = vm.createContext({document,window,URL,console,CSS:{escape:v=>v},localStorage:{getItem:k=>storage.get(k),setItem:(k,v)=>storage.set(k,v)},setTimeout:(fn,ms)=>{if(ms===900)setImmediate(fn);return 1},clearTimeout(){}});
new vm.Script(source,{filename:'index.html:inline'}).runInContext(context);
const api=window.TunPrototype,S=api.state;
const copy=v=>JSON.parse(JSON.stringify(v));
function fresh(platform='macos') { Object.assign(S,{lang:'zh',platform,group:'basic',showOther:false,template:false,busy:false,attempted:false,errors:{},draft:api.defaults(),saved:api.defaults(),feedback:null,outcome:'success',query:'',multiQueries:{},expanded:new Set(),touched:new Set()}); }
let passed=0;
async function test(name,fn) { try {fresh();await fn();passed++;console.log('PASS '+name)}catch(error){console.error('FAIL '+name);throw error} }
(async()=>{
await test('self-contained offline document and no network calls',()=>{
  assert(!/(?:src|href)\s*=\s*["']https?:/i.test(html));
  assert(!/\b(?:fetch|XMLHttpRequest|WebSocket|EventSource|sendBeacon)\s*\(/.test(source));
  assert(html.includes("connect-src 'none'"));
  assert(!html.includes('scrollIntoView'));
});
await test('35 fields and eight-part offline help for every option',()=>{
  assert.equal(api.fields.length,35);
  for(const field of api.fields){const guide=api.helpHtml(field.id);assert.equal((guide.match(/class="help-section /g)||[]).length,8,field.id);assert(guide.includes(field.key));}
});
await test('clean demo baseline is valid on all three platforms',()=>{
  for(const p of ['macos','windows','linux']){S.platform=p;assert.deepEqual(copy(api.validate()),{});}
});
await test('IPv4 and IPv6 syntax including embedded IPv4',()=>{
  for(const address of ['172.18.0.1','::1','fdfe:dcba:9876::1','::ffff:192.0.2.1'])assert(api.ip(address),address);
  for(const address of ['999.1.1.1','01.2.3.4','abc','1::2::3','1:2:3:4:5:6:7','::ffff:999.1.1.1'])assert.equal(api.ip(address),null,address);
  assert.equal(api.cidr('::1/129'),null);assert.equal(api.cidr('192.0.2.1/33'),null);
});
await test('virtual DNS derives 172.18.0.2 and optional IPv6 entry',()=>{
  assert.deepEqual(copy(api.derivedDns()),['172.18.0.2']);
  S.draft.ipv6=true;assert.deepEqual(copy(api.derivedDns()),['172.18.0.2','fdfe:dcba:9876::2']);
});
await test('no usable DNS host produces actionable validation',()=>{
  S.draft.ipv4Addresses=['172.18.0.1/32'];assert(api.validate().dnsAddresses);
});
await test('custom virtual DNS rejects outside subnet, self, broadcast and duplicate',()=>{
  S.draft.dnsAddresses='custom';for(const items of [['8.8.8.8'],['172.18.0.1'],['172.18.0.3'],['172.18.0.2','172.18.0.2'],['172.18.0.2/30']]){S.draft.dnsCustom=items;assert(api.validate().dnsAddresses,items.join(','))}
  S.draft.dnsCustom=['172.18.0.2'];assert(!api.validate().dnsAddresses);
});
await test('disabled TUN DNS ignores retained custom draft',()=>{
  S.draft.dnsMode='disabled';S.draft.dnsAddresses='custom';S.draft.dnsCustom=['bad'];assert(!api.validate().dnsAddresses);assert(api.locked(api.fields.find(f=>f.id==='dnsAddresses')));
});
await test('macOS device names require utun digits, automatic omits custom name',()=>{
  S.draft.device='custom';S.draft.custom.device='tun0';assert(api.validate().device);
  S.draft.custom.device='utun8';assert(!api.validate().device);
  S.draft.device='auto';S.draft.custom.device='bad';assert(!api.validate().device);
});
await test('CIDR lists validate address family and canonical duplicates',()=>{
  S.draft.ipv4Addresses=['::1/126'];assert(api.validate().ipv4Addresses);
  S.draft.ipv4Addresses=['172.18.0.1/30'];S.draft.ipv6=true;S.draft.ipv6Addresses=['fdfe:dcba:9876::1/126','fdfe:dcba:9876:0:0:0:0:1/126'];assert(api.validate().ipv6Addresses);
});
await test('custom MTU, UDP timeout and mapping limits are editable and bounded',()=>{
  for(const [id,bad,good] of [['mtu','1','1380'],['udpTimeout','zero','90s'],['udpNatMax','0','12288']]){S.draft[id]='custom';S.draft.custom[id]=bad;assert(api.validate()[id]);S.draft.custom[id]=good;assert(!api.validate()[id]);}
});
await test('unsupported Linux options remain locked and hidden by default',()=>{
  const f=api.fields.find(x=>x.id==='autoRedirect');assert(!api.applicable(f));assert(api.locked(f));api.setField('autoRedirect',true);assert.equal(S.draft.autoRedirect,false);
  S.group='linux';assert(!api.editorBody().includes('id="row-autoRedirect"'));S.showOther=true;const markup=api.editorBody();assert(markup.includes('id="row-autoRedirect"'));assert(/id="field-autoRedirect"[^>]*disabled/.test(markup));
});
await test('strict route is shown only on supported demo platforms',()=>{
  assert(!api.editorBody().includes('id="row-strictRoute"'));S.platform='windows';assert(api.editorBody().includes('id="row-strictRoute"'));
});
await test('auto redirect dependency and ability to turn off invalid switch',()=>{
  S.platform='linux';S.draft.autoRoute=false;assert(api.locked(api.fields.find(f=>f.id==='autoRedirect')));
  S.draft.autoRedirect=true;assert(api.validate().autoRedirect);assert(!api.locked(api.fields.find(f=>f.id==='autoRedirect')));api.setField('autoRedirect',false);assert(!api.validate().autoRedirect);
});
await test('MPTCP requires both dependencies and warns about direct traffic',()=>{
  S.platform='linux';S.draft.mptcp=true;assert(api.validate().mptcp);S.draft.autoRedirect=true;assert(!api.validate().mptcp);S.group='linux';assert(api.editorBody().includes('绕过 TUN 并直连'));
});
await test('interface and MAC include/exclude are exclusive modes',()=>{
  S.platform='linux';S.draft.interfaceMode='include';assert(api.validate().interfaceMode);S.draft.interfaceItems=['eth0'];assert(!api.validate().interfaceMode);
  S.draft.autoRedirect=true;S.draft.macMode='exclude';S.draft.macItems=['bad'];assert(api.validate().macMode);S.draft.macItems=['02:00:00:00:00:01'];assert(!api.validate().macMode);
});
await test('UID lists accept include/exclude combinations, ranges and legal intersections',()=>{
  S.platform='linux';S.draft.uidInclude=['1000','2000-2999'];S.draft.uidExclude=['1001','3000:3999'];assert(!api.validate().uidExclude);
  S.draft.uidExclude=['2500-3500'];assert(!api.validate().uidExclude);S.draft.uidExclude=['99999999999'];assert(api.validate().uidExclude);
});
await test('marks support decimal/hex and reject numeric duplicates',()=>{
  S.platform='linux';S.draft.autoRedirect=true;S.draft.markInput='custom';S.draft.custom.markInput='0x2023';S.draft.markOutput='custom';S.draft.custom.markOutput='8227';assert(api.validate().markOutput);
  S.draft.custom.markOutput='0x2024';assert(!api.validate().markOutput);
});
await test('advanced routing indexes and queue boundaries',()=>{
  S.platform='linux';S.draft.autoRedirect=true;S.draft.nfqueue='custom';S.draft.custom.nfqueue='65536';assert(api.validate().nfqueue);S.draft.custom.nfqueue='0';assert(!api.validate().nfqueue);
  S.draft.ruleIndex='custom';S.draft.custom.ruleIndex='9000';S.draft.fallbackIndex='custom';S.draft.custom.fallbackIndex='09000';assert(api.validate().fallbackIndex);
});
await test('namespace modes validate name or absolute path without probing local files',()=>{
  S.platform='linux';S.draft.netns='existing';S.draft.namespaceName='bad/name';assert(api.validate().netns);S.draft.namespaceName='work-net';assert(!api.validate().netns);
  S.draft.netns='path';S.draft.namespaceName='relative';assert(api.validate().netns);S.draft.namespaceName='/var/run/netns/work-net';assert(!api.validate().netns);
});
await test('capture rule sets reject missing and contradictory references',()=>{
  S.draft.routeSet=['unknown'];assert(api.validate().routeSet);S.draft.routeSet=['geoip-cn'];S.draft.routeExcludeSet=['geoip-cn'];assert(api.validate().routeExcludeSet);
  S.draft.routeExcludeSet=['office-net'];assert(!api.validate().routeExcludeSet);
});
await test('rule-set dialog validates local/remote sources and unique names',()=>{
  S.editingRule=null;assert(api.validateRule({name:'office-net',source:'local',local:'office-net.json'}).name);
  assert(api.validateRule({name:'new-net',source:'remote',remote:'ftp://example.com/a'}).location);
  assert(api.validateRule({name:'new-net',source:'remote',remote:'https://user:pass@example.com/a'}).location);
  assert.deepEqual(copy(api.validateRule({name:'new-net',source:'remote',remote:'https://example.com/a.srs'})),{});
});
await test('rule edit preserves selected references after rename',()=>{
  S.editingRule='office-net';S.ruleDraft={name:'office-v2',source:'local',format:'source',local:'new.json',remote:''};S.draft.routeSet=['office-net'];api.submitRule();assert.deepEqual(copy(S.draft.routeSet),['office-v2']);assert(S.draft.resources.some(r=>r.name==='office-v2'));
});
await test('dirty state includes custom inputs, resources and dormant drafts',()=>{
  S.draft.custom.mtu='1380';assert(api.changedKeys().includes('mtu'));S.draft.resources.push({name:'new',source:'local',format:'source',location:'new.json'});assert(api.changedKeys().includes('resources'));
});
await test('language, theme and platform switching preserve draft values',()=>{
  S.draft.mtu='custom';S.draft.custom.mtu='1380';S.draft.ipv4Addresses.push('10.7.0.1/30');const before=JSON.stringify(S.draft);S.lang='en';S.theme='dark';S.platform='linux';api.renderAll();assert.equal(JSON.stringify(S.draft),before);
});
await test('template mode blocks edits, saves and reset mutations',async()=>{
  S.draft.stack='system';S.template=true;const before=JSON.stringify(S.draft),saved=JSON.stringify(S.saved);api.setField('stack','mixed');await api.save();S.confirm={kind:'reset'};api.executeConfirm();assert.equal(JSON.stringify(S.draft),before);assert.equal(JSON.stringify(S.saved),saved);assert(api.editorBody().includes('完整配置模板正在接管'));
});
await test('invalid save targets field and does not update saved snapshot',async()=>{
  S.draft.device='custom';S.draft.custom.device='tun0';await api.save();assert(S.attempted);assert(S.errors.device);assert.equal(S.saved.device,'auto');assert(S.expanded.has('interface'));
});
await test('successful simulated save retains TUN-off semantics and clean state',async()=>{
  S.draft.stack='system';await api.save();assert.equal(S.saved.stack,'system');assert.equal(api.changedKeys().length,0);assert.equal(S.feedback,'success');assert(document.getElementById('runtime').innerHTML.includes('TUN 已关闭'));
});
await test('failed apply preserves saved snapshot and edited draft; retry succeeds',async()=>{
  S.draft.stack='system';S.outcome='failure';await api.save();assert.equal(S.saved.stack,'auto');assert.equal(S.draft.stack,'system');assert.equal(S.feedback,'failure');assert(api.changedKeys().length);await api.save();assert.equal(S.saved.stack,'system');assert.equal(S.feedback,'success');
});
await test('apply operation rejects re-entry',async()=>{
  S.draft.stack='system';const first=api.save();assert(S.busy);await api.save();await first;assert.equal(S.saved.stack,'system');
});
await test('cancel rolls all groups back to the saved snapshot',()=>{
  S.draft.stack='mixed';S.draft.udpTimeout='custom';S.draft.custom.udpTimeout='90s';S.confirm={kind:'cancel'};api.executeConfirm();assert.equal(api.changedKeys().length,0);
});
await test('group reset retains other groups and rule-set library',()=>{
  S.group='capture';S.draft.routeSet=['geoip-cn'];S.draft.stack='system';S.draft.resources.push({name:'new',source:'local',format:'source',location:'new.json'});S.confirm={kind:'reset'};api.executeConfirm();assert.equal(S.draft.routeSet.length,0);assert.equal(S.draft.stack,'system');assert(S.draft.resources.some(r=>r.name==='new'));
});
await test('removing rule set clears both selected reference arrays',()=>{
  S.draft.routeSet=['geoip-cn'];S.draft.routeExcludeSet=['office-net'];S.confirm={kind:'removeRule',name:'geoip-cn'};api.executeConfirm();assert.equal(S.draft.routeSet.length,0);assert.equal(S.draft.routeExcludeSet[0],'office-net');
});
await test('all group/locale/platform states generate populated markup',()=>{
  for(const lang of ['zh','en'])for(const platform of ['macos','windows','linux'])for(const group of ['basic','dns','udp','capture','linux','help']){Object.assign(S,{lang,platform,group});const markup=api.editorBody();assert(markup.includes('id="group-title"'));assert(!markup.includes('{{'));assert(!markup.includes('undefined'));}
});
await test('mobile drawer, breakpoints, keyboard and reduced motion are present',()=>{
  for(const token of ['@media(max-width:1100px)','@media(max-width:760px)','@media(max-width:440px)','prefers-reduced-motion','help-dialog','aria-live="polite"','focus-visible'])assert(html.includes(token),token);
  assert(source.includes("event.key==='F1'"));assert(source.includes("event.key.toLowerCase()==='s'"));assert(source.includes("'ArrowDown'"));
});
await test('delegated primary flow edits a select, custom input and list',()=>{
  const stack=new Element('field-stack');stack.dataset.key='stack';stack.value='system';emit('change',stack);assert.equal(S.draft.stack,'system');
  const mtu=new Element('field-mtu');mtu.dataset.key='mtu';mtu.value='custom';emit('change',mtu);const input=new Element('custom-mtu');input.dataset.custom='mtu';input.value='1380';emit('input',input);emit('change',input);assert.equal(S.draft.custom.mtu,'1380');assert(!S.errors.mtu);
  const add=new Element('add-loopback');add.dataset.addList='loopback';emit('click',add);assert.equal(S.draft.loopback.length,1);const address=new Element('entry-loopback-0');address.dataset.list='loopback';address.dataset.index='0';address.value='10.7.0.1';emit('input',address);emit('change',address);assert.equal(S.draft.loopback[0],'10.7.0.1');
});
await test('demo platform/source events preserve draft and lock template mode',()=>{
  S.draft.stack='system';const platform=new Element('demo-platform');platform.value='linux';emit('change',platform);assert.equal(S.platform,'linux');assert.equal(S.draft.stack,'system');const input=new Element('demo-source');input.value='template';emit('change',input);assert(S.template);assert(api.locked(api.fields[0]));
});
await test('help search matches detailed scenarios and handles no results',()=>{
  S.group='help';const search=new Element('help-query');search.value='游戏';emit('input',search);assert(document.getElementById('help-results').innerHTML.includes('udpMapping'));
  search.value='no-match-1234567890';emit('input',search);assert(document.getElementById('help-results').innerHTML.includes('没有匹配项'));
});
await test('rendered English help contains no untranslated Chinese metadata',()=>{
  S.lang='en';for(const field of api.fields){S.help=field.id;api.renderAll();const markup=document.getElementById('help-pane').innerHTML;assert(!/[\u4e00-\u9fff]/.test(markup),field.id)}
});
await test('rendered editor inspection identifiers are unique kebab case',()=>{
  for(const group of ['basic','dns','udp','capture','linux']){S.group=group;S.platform='linux';api.renderAll();const ids=[...document.getElementById('editor').innerHTML.matchAll(/data-od-id="([^"]+)"/g)].map(m=>m[1]);assert.equal(ids.length,new Set(ids).size);assert(ids.every(id=>/^[a-z\d]+(?:-[a-z\d]+)*$/.test(id)),group)}
});
await test('error references exist in generated markup even before a save attempt',()=>{
  S.group='linux';S.platform='linux';S.draft.autoRedirect=true;S.draft.autoRoute=false;api.renderAll();const markup=document.getElementById('editor').innerHTML;assert(markup.includes('id="err-autoRedirect"'));assert(markup.includes('aria-invalid="true"'));
});
await test('list labels resolve to an input or add-entry control',()=>{
  S.group='capture';api.renderAll();let markup=document.getElementById('editor').innerHTML;assert(markup.includes('for="add-routeAddress"'));assert(markup.includes('id="add-routeAddress"'));
  S.group='basic';S.expanded.add('interface');api.renderAll();markup=document.getElementById('editor').innerHTML;assert(markup.includes('for="entry-ipv4Addresses-0"'));assert(markup.includes('id="entry-ipv4Addresses-0"'));
});
await test('keyboard navigation and save shortcut execute model flow',async()=>{
  const nav=new Element('nav-basic');nav.dataset.group='basic';emit('keydown',nav,{key:'ArrowRight'});assert.equal(S.group,'dns');S.draft.stack='system';const event=emit('keydown',new Element('field-stack'),{key:'s',ctrlKey:true});assert(event.defaultPrevented);await new Promise(setImmediate);assert.equal(S.saved.stack,'system');
});
await test('static grid constraints fit the required viewport matrix',()=>{
  for(const width of [360,390,430,600,768,820,1024,1366,1440,1920]){const inner=Math.min(width,1328)-(width<=760?32:48),editor=width<=760?inner:width<=1100?inner-152-20:inner-(width>=1660?180+300:168+284)-48;assert(editor>0);if(width>440){const control=width<=760?Math.max(168,editor*.42):width<=1100?228:width>=1660?232:216,gap=width<=760?20:24;assert(editor-control-gap>=150,'label width at '+width)}}
});
await test('text blur updates validation without replacing clicked editor controls',()=>{
  api.setField('mtu','custom');const editor=document.getElementById('editor'),markup=editor.innerHTML,input=new Element('custom-mtu');input.dataset.custom='mtu';input.value='1380';emit('input',input);emit('change',input);assert.equal(editor.innerHTML,markup);assert(!S.errors.mtu);
});
await test('token contract references resolve and component colors use semantic roles',()=>{
  const css=html.match(/<style>([\s\S]*?)<\/style>/)[1],rootStart=css.indexOf(':root {'),rootEnd=css.indexOf('\n}',rootStart)+2,components=css.slice(rootEnd);assert(!/#[\da-f]{3,8}\b/i.test(components));const defined=new Set([...css.matchAll(/(--[\w-]+)\s*:/g)].map(m=>m[1]));for(const match of css.matchAll(/var\((--[\w-]+)/g))assert(defined.has(match[1]),match[1]);assert(css.includes('.chip button{min-height:44px;min-width:44px}'));
});
console.log('\n'+passed+' checks passed. Model and generated-markup verification only; no browser layout or network verification.');
})().catch(error=>{console.error(error);process.exitCode=1});
