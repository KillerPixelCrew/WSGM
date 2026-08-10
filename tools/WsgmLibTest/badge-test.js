// Badge live test — matches SteamPageBridge.InstallBadgeScript (new wide-hero
// detection). Resets any prior badge, maps the current game (3602290) to a test
// card, installs the resident observer, and renders. Leaving the game (grid has no
// wide hero) should clear it.
(()=>{try{
var W=window.__wsgm=window.__wsgm||{};
try{if(W.badgeObserver)W.badgeObserver.disconnect();}catch(e){}
var old=document.getElementById('wsgm-card-badge');if(old)old.remove();
W.badgeInstalled=false;
W.cardMap={"3602290":"Steam Deck 512GB"};
if(!W.badgeInstalled){W.badgeInstalled=true;
var BID='wsgm-card-badge';
var curId=function(){try{var imgs=document.querySelectorAll('img');var best=0,bestW=0;
for(var k=0;k<imgs.length;k++){var i=imgs[k];var r=i.getBoundingClientRect();
if(r.width<600||r.width<=r.height)continue;var m=(i.src||'').match(/assets\/(\d+)\//);
if(m&&r.width>bestW){bestW=r.width;best=Number(m[1]);}}return best;}catch(e){return 0;}};
var remove=function(){var b=document.getElementById(BID);if(b)b.remove();};
var render=function(){try{var id=curId();var map=W.cardMap||{};var name=id&&map[id];
if(!name){remove();return;}var b=document.getElementById(BID);
if(!b){b=document.createElement('div');b.id=BID;b.className='wsgm-badge';
b.style.cssText='position:fixed;top:16px;left:16px;z-index:99999;display:inline-flex;align-items:center;gap:6px;padding:5px 12px;border-radius:5px;background:rgba(20,25,32,.9);color:#e6edf3;font-size:14px;font-weight:600;box-shadow:0 2px 10px rgba(0,0,0,.5);pointer-events:none;';
document.body.appendChild(b);}b.textContent='◉ On: '+name;}catch(e){}};
W.renderBadge=render;
try{var obs=new MutationObserver(function(){render();});
obs.observe(document.body,{childList:true,subtree:true,attributes:true,attributeFilter:['src']});
W.badgeObserver=obs;}catch(e){}
render();}
return JSON.stringify({ok:true,currentApp:W.renderBadge?1:0});
}catch(e){return JSON.stringify({ok:false,err:String(e)});}})()
