using System.Text.Json;
using WheelWizard.DiscordPresence;

namespace WheelWizard.LoungeStats;

/// <summary>
/// The page the OBS Browser source shows, and the small answer it asks the app for every two seconds. The app does the
/// reading of RR Lounge; the page only draws, and only rebuilds when the numbers or the look changed.
/// </summary>
public static class LoungeStatsPage
{
    public static string Url => $"http://127.0.0.1:{RoomLinkListener.Port}/lounge-stats";

    /// <summary>The address the page loads the rank badge from: the app's saved copy, small and offline-safe.</summary>
    public const string BadgePath = "/lounge-stats/badge";
    public const string GlyphPath = "/lounge-stats/glyph/";

    /// <summary>Everything the page draws, as JSON. Colours only travel as checked hex values.</summary>
    public static string State(LoungeStatsService service, LoungeStatsConfig config)
    {
        var stats = service.Current;
        var change = service.LastChange;
        string Hex(string path, string fallback) => config.Color(path, fallback);
        var look = new
        {
            style = config.GetString("layout.style") == "hud" ? "hud" : "card",
            direction = config.GetString("layout.direction") == "horizontal" ? "horizontal" : "vertical",
            scale = Math.Clamp(config.GetNumber("layout.scale"), 0.4, 3),
            spacing = Math.Clamp(config.GetNumber("layout.spacing"), 0, 60),
            textSize = Math.Clamp(config.GetNumber("layout.textSize"), 0.5, 3),
            opacity = Math.Clamp(config.GetNumber("layout.opacity"), 0.1, 1),
            anchor = LoungeStatsConfig.Anchors.Contains(config.GetString("layout.anchor")) ? config.GetString("layout.anchor") : "top left",
            x = Math.Clamp(config.GetNumber("layout.x"), -4000, 4000),
            y = Math.Clamp(config.GetNumber("layout.y"), -4000, 4000),
            background = LoungeStatsConfig.BackgroundModes.Contains(config.GetString("background.mode"))
                ? config.GetString("background.mode")
                : "solid",
            backgroundColor = Hex("background.color", "#0E1220"),
            backgroundOpacity = Math.Clamp(config.GetNumber("background.opacity"), 0, 1),
            radius = Math.Clamp(config.GetNumber("background.radius"), 0, 60),
            text = Hex("colors.text", "#FFFFFF"),
            label = Hex("colors.label", "#9AA3BB"),
            mmr = Hex("colors.mmr", "#FFFFFF"),
            rank = config.RankColor(stats),
            wins = Hex("colors.wins", "#5EF0A5"),
            losses = Hex("colors.losses", "#FF6D7E"),
            hudFill = Hex("hud.fill", "#FFE21A"),
            hudFillBottom = Hex("hud.fillBottom", "#E8A800"),
            hudOutline = Hex("hud.outline", "#141414"),
            hudDepth = Hex("hud.depth", "#6B6400"),
            hudDepthSize = (int)Math.Clamp(config.GetNumber("hud.depthSize"), 0, 10),
            hudBadge = config.GetBool("hud.emblem"),
            badgeOutline = Math.Clamp(config.GetNumber("hud.badgeOutline"), 0, 24),
            badgeOutlineColor = Hex("hud.emblemEdge", "#000000"),
            hudGlyphs = "0123456789-"
                .Where(c => HudArt.GlyphUri(c, HudLook.From(config, stats)) is not null)
                .ToDictionary(
                    c => c.ToString(),
                    c =>
                        (object)
                            new
                            {
                                u = $"{GlyphPath}{(c == '-' ? "dash" : c.ToString())}?k={Hex("hud.fill", "#FFE21A").TrimStart('#')}{Hex("hud.fillBottom", "#E8A800").TrimStart('#')}",
                                w = HudArt.GlyphWidth(c, HudLook.From(config, stats)),
                            }
                ),
            hudGlyphOverlap = HudArt.GlyphOverlap,
            animations = config.GetBool("effects.animations"),
            showRank = config.GetBool("show.rank"),
            showBadge = config.GetBool("show.badge"),
            showFlag = config.GetBool("show.flag"),
            showTag = config.GetBool("show.tag"),
            showMmr = config.GetBool("show.mmr"),
            showWins = config.GetBool("show.wins"),
            showLosses = config.GetBool("show.losses"),
            showPlace = config.GetBool("show.place"),
            showLabels = config.GetBool("show.labels"),
        };
        var data = stats is null
            ? null
            : new
            {
                name = stats.Name,
                flag = stats.FlagUrl,
                badge = stats.BadgeUrl is { } badge ? $"{BadgePath}?v={Uri.EscapeDataString(badge)}" : null,
                division = stats.Division?.ToUpperInvariant(),
                place = stats.Place,
                mmr = stats.Mmr,
                wins = stats.Wins,
                losses = stats.Losses,
            };
        var visible = stats is not null && config.GetBool("show.component");
        var body = new
        {
            visible,
            look,
            stats = data,
        };
        var version = JsonSerializer.Serialize(body).GetHashCode().ToString("x");
        return JsonSerializer.Serialize(
            new
            {
                visible,
                version,
                changeNumber = service.ChangeNumber,
                change = change is null
                    ? null
                    : new
                    {
                        mmr = change.MmrDelta,
                        wins = change.WinsUp,
                        losses = change.LossesUp,
                        rank = change.RankDirection,
                    },
                look,
                stats = data,
            }
        );
    }

    public const string Html = """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>RR Lounge stats</title>
        <style>
        html,body{margin:0;background:transparent;overflow:hidden;width:100vw;height:100vh}
        #root{position:fixed;opacity:0;transition:opacity .4s;font-family:"Segoe UI",Roboto,Arial,sans-serif}
        #root.on{opacity:var(--opacity,1)}
        .box{display:inline-block;box-sizing:border-box}
        .row{display:flex;align-items:center}
        .col{display:flex;flex-direction:column}
        .label{font-weight:700;letter-spacing:1.2px}
        .cell{border-radius:8px;padding:2px 4px;transition:background-color .7s,transform .45s}
        .flag{border-radius:2px;object-fit:cover}
        .pill{opacity:0;transition:opacity .4s;font-weight:800}
        .pill.on{opacity:1}
        .shadow{text-shadow:1.5px 1.5px 2px rgba(0,0,0,.86)}
        .hud{position:relative;display:inline-block;font-family:"Arial Black","Segoe UI Black","Segoe UI",sans-serif;font-weight:900;line-height:1;white-space:nowrap}
        .hud span{position:absolute;left:0;top:0}
        .hud span.size{position:relative;visibility:hidden}
        .hud .fill{background:linear-gradient(var(--top) 35%,var(--bottom));-webkit-background-clip:text;background-clip:text;color:transparent}
        </style></head>
        <body><div id="root"></div>
        <script>
        const root=document.getElementById('root');
        let version=null,shown=-1,state=null,timer=null;
        const esc=t=>String(t??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
        const fmt=n=>Number(n).toLocaleString('en-US');
        const cnt=n=>n==null?'-':String(n);
        function outlineFilter(px,c){
          if(!(px>0))return '';
          // A solid outline: grow the picture's shape and fill the growth with the color, with the picture on top.
          let f=document.getElementById('badgeoutline');
          if(!f){
            document.body.insertAdjacentHTML('beforeend','<svg width="0" height="0" style="position:absolute"><filter id="badgeoutline" x="-30%" y="-30%" width="160%" height="160%" color-interpolation-filters="sRGB"><feMorphology in="SourceAlpha" operator="dilate" radius="1" result="grown"/><feFlood flood-color="#000" result="ink"/><feComposite in="ink" in2="grown" operator="in" result="line"/><feMerge><feMergeNode in="line"/><feMergeNode in="SourceGraphic"/></feMerge></filter></svg>');
            f=document.getElementById('badgeoutline');
          }
          f.querySelector('feMorphology').setAttribute('radius',String(Math.max(.5,px)));
          f.querySelector('feFlood').setAttribute('flood-color',c);
          return 'filter:url(#badgeoutline);';
        }
        function hud(text,size,l){
          const g=l.hudGlyphs;
          if(g&&text.length&&[...text].every(c=>g[c])){
            const h=size*1.1,over=h*l.hudGlyphOverlap/84;
            const imgs=[...text].map((c,i)=>`<img src="${g[c].u}" style="height:${h}px;width:${h*g[c].w/84}px;margin-right:${i<text.length-1?-over:0}px;image-rendering:auto">`).join('');
            return `<span class="hud" data-hud="1" style="display:inline-flex;align-items:center;vertical-align:middle">${imgs}</span>`;
          }
          const o=Math.max(1.5,size*.11),st=Math.max(1,size*.035);let h='';
          for(let i=l.hudDepthSize;i>=1;i--){
            const tr=`transform:translate(${i*st}px,${i*st}px)`;
            h+=`<span style="${tr};color:transparent;-webkit-text-stroke:${o*2}px ${l.hudOutline}">${esc(text)}</span>`;
            h+=`<span style="${tr};color:${l.hudDepth}">${esc(text)}</span>`;
          }
          h+=`<span style="color:transparent;-webkit-text-stroke:${o*2}px ${l.hudOutline}">${esc(text)}</span>`;
          h+=`<span class="fill">${esc(text)}</span>`;
          return `<span class="hud" data-hud="1" style="font-size:${size}px;padding:${o}px;margin-right:${l.hudDepthSize*st}px;margin-bottom:${l.hudDepthSize*st}px;--top:${l.hudFill};--bottom:${l.hudFillBottom}"><span class="size">${esc(text)}</span>${h}</span>`;
        }
        function bg(l){
          if(l.background==='transparent')return 'padding:4px';
          const a=Math.round(l.backgroundOpacity*255).toString(16).padStart(2,'0');
          if(l.background==='blurred'){
            const b=Math.round(l.backgroundOpacity*.55*255).toString(16).padStart(2,'0');
            return `padding:10px 14px;border-radius:${l.radius}px;background:linear-gradient(135deg,rgba(255,255,255,.14),${l.backgroundColor}${b});backdrop-filter:blur(14px);-webkit-backdrop-filter:blur(14px);border:1px solid rgba(255,255,255,.28)`;
          }
          return `padding:10px 14px;border-radius:${l.radius}px;background:${l.backgroundColor}${a}`;
        }
        function card(s,l){
          const t=l.textSize,g=l.spacing,horiz=l.direction==='horizontal';
          const lab=x=>l.showLabels?`<div class="label" style="font-size:${10.5*t}px;color:${l.label}">${x}</div>`:'';
          let who='';
          if(l.showFlag&&s.flag)who+=`<img class="flag" src="${esc(s.flag)}" style="width:${24*t}px;height:${16*t}px">`;
          if(l.showTag)who+=`<span style="font-size:${17*t}px;font-weight:700;color:${l.text}">${esc(s.name)}</span>`;
          if(l.showPlace&&s.place!=null)who+=`<span style="font-size:${14*t}px;font-weight:600;color:${l.label}">#${s.place}</span>`;
          who=who?`<div class="row" style="gap:${g*.6}px">${who}</div>`:'';
          let rank='';
          if(l.showRank&&(s.division||s.badge)){
            let r='';
            if(l.showBadge&&s.badge)r+=`<img src="${esc(s.badge)}" style="width:${26*t}px;height:${26*t}px">`;
            if(s.division)r+=`<span style="font-size:${14*t}px;font-weight:900;color:${l.rank}">${esc(s.division)}</span>`;
            rank=`<div class="cell row" id="rank" style="gap:${6*t}px">${r}</div>`;
          }
          const mmr=l.showMmr?`<div class="cell" id="mmr">${lab('MMR')}<div class="row" style="gap:${6*t}px"><span id="mmrv" style="font-size:${(horiz?20:26)*t}px;font-weight:900;color:${l.mmr}">${fmt(s.mmr)}</span><span class="pill" id="pill" style="font-size:${12*t}px"></span></div></div>`:'';
          const w=l.showWins?`<div class="cell" id="wins">${lab('WINS')}<span style="font-size:${18*t}px;font-weight:900;color:${l.wins}">${cnt(s.wins)}</span></div>`:'';
          const lo=l.showLosses?`<div class="cell" id="losses">${lab('LOSSES')}<span style="font-size:${18*t}px;font-weight:900;color:${l.losses}">${cnt(s.losses)}</span></div>`:'';
          if(horiz)return `<div class="row" style="gap:${g*1.6}px">${who}${rank}${mmr}${w}${lo}</div>`;
          const top=who||rank?`<div class="row" style="gap:${g}px">${who}${rank}</div>`:'';
          const rec=w||lo?`<div class="row" style="gap:${g*2}px">${w}${lo}</div>`:'';
          return `<div class="col" style="gap:${g}px">${top}${mmr}${rec}</div>`;
        }
        function hudLook(s,l){
          const t=l.textSize,g=l.spacing,horiz=l.direction==='horizontal';
          let rank='';
          if(l.showRank){
            if(l.hudBadge&&s.badge)rank=`<div class="cell" id="rank"><img src="${esc(s.badge)}" style="height:${64*t}px;width:auto;display:block;${outlineFilter(l.badgeOutline*.25*t,l.badgeOutlineColor)}"></div>`;
          }
          let who='';
          if(l.showFlag&&s.flag)who+=`<img class="flag" src="${esc(s.flag)}" style="width:${21*t}px;height:${14*t}px">`;
          if(l.showTag)who+=`<span class="shadow" style="font-size:${14*t}px;font-weight:700;color:${l.text}">${esc(s.name)}</span>`;
          if(l.showPlace&&s.place!=null)who+=`<span class="shadow" style="font-size:${13*t}px;font-weight:700;color:${l.text}">#${s.place}</span>`;
          who=who?`<div class="row" style="gap:${6*t}px">${who}</div>`:'';
          let line='';
          if(l.showMmr)line+=`<div class="cell" id="mmr"><span id="mmrv">${hud(String(s.mmr),38*t,l)}</span></div><span class="pill shadow" id="pill" style="font-size:${14*t}px"></span>`;
          if(l.showWins)line+=`<div class="cell" id="wins">${hud((l.showLabels?'W':'')+cnt(s.wins),20*t,l)}</div>`;
          if(l.showLosses)line+=`<div class="cell" id="losses">${hud((l.showLabels?'L':'')+cnt(s.losses),20*t,l)}</div>`;
          line=line?`<div class="row" style="gap:${g}px">${line}</div>`:'';
          const nums=who||line?`<div class="col" style="align-items:${horiz?'flex-start':'center'}">${who}${line}</div>`:'';
          return horiz?`<div class="row" style="gap:${g}px">${rank}${nums}</div>`:`<div class="col" style="gap:${g*.5}px;align-items:center">${rank}${nums}</div>`;
        }
        function place(l){
          const a=l.anchor.split(' '),v=a[0],h=a.length>1?a[1]:'center';
          root.style.left=root.style.right=root.style.top=root.style.bottom='';
          root.style.transform='';
          let ox='0%',oy='0%';const tf=[];
          if(h==='left'){root.style.left=l.x+'px'}else if(h==='right'){root.style.right=l.x+'px';ox='100%'}else{root.style.left=`calc(50% + ${l.x}px)`;tf.push('translateX(-50%)');ox='50%'}
          if(v==='top'){root.style.top=l.y+'px'}else if(v==='bottom'){root.style.bottom=l.y+'px';oy='100%'}else{root.style.top=`calc(50% + ${l.y}px)`;tf.push('translateY(-50%)');oy='50%'}
          tf.push(`scale(${l.scale})`);
          root.style.transform=tf.join(' ');root.style.transformOrigin=`${ox} ${oy}`;
          root.style.setProperty('--opacity',l.opacity);
        }
        function draw(st){
          const l=st.look,s=st.stats;
          place(l);
          if(!st.visible||!s){root.classList.remove('on');return}
          root.innerHTML=`<div class="box" style="${bg(l)}">${l.style==='hud'?hudLook(s,l):card(s,l)}</div>`;
          root.classList.add('on');
        }
        function flash(id,color,hold){
          const el=document.getElementById(id);if(!el)return;
          el.style.transition='none';el.style.backgroundColor=color+'61';
          requestAnimationFrame(()=>{el.style.transition='';setTimeout(()=>el.style.backgroundColor='transparent',hold||900)});
        }
        function countUp(from,to,l){
          const el=document.getElementById('mmrv');if(!el)return;
          const start=performance.now();
          const step=now=>{
            const t=Math.min(1,(now-start)/900),e=1-Math.pow(1-t,3),v=Math.round(from+(to-from)*e);
            el.innerHTML=l.style==='hud'?hud(String(v),38*l.textSize,l):fmt(v);
            if(t<1)requestAnimationFrame(step);
          };
          requestAnimationFrame(step);
        }
        function animate(st){
          const c=st.change,l=st.look,s=st.stats;if(!c||!s||!l.animations)return;
          if(c.mmr){
            const up=c.mmr>0;flash('mmr',up?l.wins:l.losses);countUp(s.mmr-c.mmr,s.mmr,l);
            const p=document.getElementById('pill');
            if(p){p.textContent=(up?'+':'')+c.mmr;p.style.color=up?l.wins:l.losses;requestAnimationFrame(()=>p.classList.add('on'));setTimeout(()=>p.classList.remove('on'),6000)}
          }
          if(c.wins)flash('wins',l.wins);
          if(c.losses)flash('losses',l.losses);
          if(c.rank){
            flash('rank',l.rank,1600);const r=document.getElementById('rank');
            if(r){r.style.transform=`scale(${c.rank>0?1.25:.8})`;setTimeout(()=>r.style.transform='scale(1)',600)}
          }
        }
        async function tick(){
          try{
            const res=await fetch('/lounge-stats/state',{cache:'no-store'});
            const st=await res.json();
            if(st.version!==version){version=st.version;draw(st)}
            if(shown>=0&&st.changeNumber>shown)animate(st);
            shown=st.changeNumber;
          }catch(e){/* The app is closed or busy; the last stats stay. */}
          timer=setTimeout(tick,2000);
        }
        tick();
        </script></body></html>
        """;
}
