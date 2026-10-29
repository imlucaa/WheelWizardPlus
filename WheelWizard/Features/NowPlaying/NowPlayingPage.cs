using System.Text.Json;
using WheelWizard.DiscordPresence;

namespace WheelWizard.NowPlaying;

/// <summary>The page the OBS Browser source shows, and the small answer it asks for every second.</summary>
public static class NowPlayingPage
{
    public static string Url => $"http://127.0.0.1:{RoomLinkListener.Port}/now-playing";

    /// <summary>
    /// What the page shows at <paramref name="now"/>, in the look from <paramref name="config"/>. The version changes with the
    /// song, so the page reloads the cover only then.
    /// </summary>
    public static string State(NowPlayingInfo? info, NowPlayingConfig config, DateTimeOffset now) =>
        JsonSerializer.Serialize(
            new
            {
                visible = info is not null && !config.IsHidden(info),
                playing = info?.IsPlaying ?? false,
                title = info?.Title ?? string.Empty,
                artist = config.GetBool("show.artist") ? info?.Artist ?? string.Empty : string.Empty,
                app = config.GetBool("show.app") ? info?.App ?? string.Empty : string.Empty,
                cover = config.GetBool("show.cover") && info?.Cover is { Length: > 0 },
                color = config.CardColor(info?.Cover),
                position = info?.ElapsedAt(now) ?? 0,
                duration = info?.Duration ?? 0,
                version = info is null ? 0 : HashCode.Combine(info.Title, info.Artist, info.Cover?.Length ?? 0) & 0x7fffffff,
                look = new
                {
                    style = config.GetString("layout.style"),
                    coverSide = config.GetString("layout.coverSide"),
                    width = config.GetNumber("layout.width"),
                    scale = config.GetNumber("layout.scale"),
                    radius = config.GetNumber("layout.radius"),
                    time = config.GetBool("show.time"),
                    bars = config.GetBool("show.bars"),
                    progress = config.GetBool("show.progress"),
                    text = SafeColor(config.GetString("colors.text"), "#FFFFFF"),
                    progressColor = SafeColor(config.GetString("colors.progress"), "#FFFFFF"),
                    cardOpacity = config.GetNumber("colors.cardOpacity"),
                    coverShape = config.GetString("layout.coverShape"),
                    coverSize = config.GetNumber("layout.coverSize"),
                    gap = config.GetNumber("layout.gap"),
                    textSize = config.GetNumber("layout.textSize"),
                    align = config.GetString("layout.align"),
                    glow = config.GetBool("effects.glow"),
                    spin = config.GetBool("effects.spin"),
                    borderWidth = config.GetNumber("effects.borderWidth"),
                    borderColor = SafeColor(config.GetString("effects.borderColor"), "#FFFFFF"),
                },
            }
        );

    private static string SafeColor(string hex, string fallback) =>
        NowPlayingConfig.IsHex(hex) ? NowPlayingConfig.Normalize(hex) : fallback;

    public static string StateFor(NowPlayingService service, NowPlayingConfig config) =>
        State(service.Current, config, DateTimeOffset.UtcNow);

    public const string Html = """
        <!doctype html>
        <html>
        <head>
        <meta charset="utf-8">
        <title>Now playing</title>
        <style>
          html, body { margin: 0; background: transparent; font-family: 'Segoe UI', Roboto, Arial, sans-serif; overflow: hidden; }
          #card { box-sizing: border-box; width: 360px; padding: 10px; opacity: 0; transform: translateY(8px); transform-origin: 0 0;
                  transition: opacity .4s ease, transform .4s ease; --tint: 74, 79, 94; --text: #fff; --text-rgb: 255, 255, 255;
                  --alpha: .92; --radius: 10px; --cover: 90px; --progress: #fff; --gap: 8px; --ts: 1; --bw: 0px; --bc: #fff; color: var(--text); }
          #card.show { opacity: 1; transform: none; }
          .top { display: flex; gap: var(--gap); align-items: flex-start; }
          #card.right .top { flex-direction: row-reverse; }
          #cover { width: var(--cover); height: var(--cover); border-radius: var(--radius); object-fit: cover; flex: none;
                   background: rgba(255, 255, 255, 0.2); box-shadow: 0 0 22px rgba(var(--tint), 0.6); border: var(--bw) solid var(--bc); box-sizing: border-box; }
          #cover.none { display: none; }
          .right { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: calc(var(--gap) * .75); }
          .c { border-radius: var(--radius); background: rgba(var(--tint), var(--alpha)); box-sizing: border-box; border: var(--bw) solid var(--bc); }
          .title-card { padding: 7px 12px; }
          .title-card div { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
          #title { font-size: calc(15px * var(--ts)); font-weight: 800; }
          #artist { font-size: calc(12px * var(--ts)); color: rgba(var(--text-rgb), .9); margin-top: 1px; }
          #artist small { color: rgba(var(--text-rgb), .6); font-size: calc(12px * var(--ts)); }
          .time-card { display: flex; align-items: center; gap: 8px; padding: 0 12px; height: 32px; font-size: calc(12px * var(--ts)); font-weight: 800; }
          .time-card.hidden { display: none; }
          .time-card .t.hidden, .bars.hidden { display: none; }
          .bars { flex: 1; display: flex; justify-content: center; align-items: center; gap: 3px; height: 22px; }
          .bars i { width: 3px; height: 4px; border-radius: 2px; background: var(--text); opacity: .9; animation: eq 1s ease-in-out infinite; animation-play-state: paused; }
          .playing .bars i { animation-play-state: running; }
          .bars i:nth-child(5n+1) { animation-duration: .9s; animation-delay: -.2s; }
          .bars i:nth-child(5n+2) { animation-duration: 1.3s; animation-delay: -.7s; }
          .bars i:nth-child(5n+3) { animation-duration: .7s; animation-delay: -.4s; }
          .bars i:nth-child(5n+4) { animation-duration: 1.1s; animation-delay: -.9s; }
          .bars i:nth-child(5n)   { animation-duration: 1.5s; animation-delay: -.1s; }
          @keyframes eq { 0%, 100% { height: 4px; } 50% { height: 16px; } }
          #track { height: 5px; border-radius: min(var(--radius), 2.5px); background: color-mix(in srgb, var(--progress) 25%, transparent); margin-top: 8px; overflow: hidden; }
          #track.none { display: none; }
          #fill { height: 100%; width: 0; background: var(--progress); }
          #card.compact .time-card { display: none; }
          #card.strip .time-card, #card.strip #artist { display: none; }
          #card.strip .right, #card.compact .right, #card.text .right { align-self: center; }
          #card.vertical .top { flex-direction: column; align-items: center; }
          #card.vertical.right .top { flex-direction: column-reverse; }
          #card.vertical .right { width: 100%; flex: none; }
          #card.center .title-card { text-align: center; }
          #card.circle #cover { border-radius: 50%; }
          #card.noglow #cover { box-shadow: none; }
          #card.spin.playing #cover { animation: turn 9s linear infinite; }
          @keyframes turn { to { transform: rotate(360deg); } }
          #card.text { }
          #card.text .c { background: transparent; }
          #card.text .title-card { padding: 4px 2px; }
          #card.text #cover { box-shadow: none; }
          #card.text .c { border-color: transparent; }
        </style>
        </head>
        <body>
        <div id="card">
          <div class="top">
            <img id="cover" class="none" alt="">
            <div class="right">
              <div class="c title-card"><div id="title"></div><div id="artist"></div></div>
              <div class="c time-card" id="times"><span id="elapsed" class="t"></span><div class="bars" id="bars"></div><span id="total" class="t"></span></div>
            </div>
          </div>
          <div id="track" class="none"><div id="fill"></div></div>
        </div>
        <script>
          var card = document.getElementById('card'), cover = document.getElementById('cover'), track = document.getElementById('track'),
              fill = document.getElementById('fill'), times = document.getElementById('times'), last = -1, state = null, fetched = 0;
          var bars = document.getElementById('bars');
          for (var i = 0; i < 22; i++) bars.appendChild(document.createElement('i'));
          function clock(seconds) {
            seconds = Math.max(0, Math.floor(seconds));
            var h = Math.floor(seconds / 3600), m = Math.floor(seconds / 60) % 60, s = seconds % 60;
            return h ? h + ':' + String(m).padStart(2, '0') + ':' + String(s).padStart(2, '0') : Math.floor(seconds / 60) + ':' + String(s).padStart(2, '0');
          }
          function rgb(hex) { var n = parseInt(hex.slice(1), 16); return (n >> 16 & 255) + ', ' + (n >> 8 & 255) + ', ' + (n & 255); }
          function paint(s) {
            var look = s.look, hasLength = s.duration > 0;
            var strip = look.style === 'strip';
            document.getElementById('title').textContent = strip && s.artist ? s.title + '  ·  ' + s.artist : s.title;
            var artist = document.getElementById('artist');
            var source = s.app ? (s.playing ? s.app : s.app + ' · Paused') : (s.playing ? '' : 'Paused');
            artist.textContent = s.artist;
            if (source) { var small = document.createElement('small'); small.textContent = (s.artist ? '  ·  ' : '') + source; artist.appendChild(small); }
            artist.style.display = artist.textContent ? '' : 'none';
            var style = card.style;
            style.setProperty('--tint', rgb(s.color));
            style.setProperty('--text', look.text);
            style.setProperty('--text-rgb', rgb(look.text));
            style.setProperty('--progress', look.progressColor);
            style.setProperty('--alpha', look.cardOpacity);
            style.setProperty('--radius', look.radius + 'px');
            style.setProperty('--cover', Math.min(look.coverSize, strip ? 64 : 220) + 'px');
            style.setProperty('--gap', look.gap + 'px');
            style.setProperty('--ts', look.textSize);
            style.setProperty('--bw', look.borderWidth + 'px');
            style.setProperty('--bc', look.borderColor);
            style.width = look.width + 'px';
            style.transform = '';
            style.zoom = look.scale;
            if (s.cover) { if (s.version !== last) cover.src = '/now-playing/cover?v=' + s.version; cover.className = ''; } else { cover.className = 'none'; }
            last = s.version;
            var showTime = look.time && hasLength;
            document.getElementById('elapsed').className = 't' + (showTime ? '' : ' hidden');
            document.getElementById('total').className = 't' + (showTime ? '' : ' hidden');
            bars.className = 'bars' + (look.bars ? '' : ' hidden');
            times.className = 'c time-card' + (showTime || look.bars ? '' : ' hidden');
            document.getElementById('total').textContent = hasLength ? clock(s.duration) : '';
            track.className = look.progress && hasLength ? '' : 'none';
            card.className = 'show ' + look.style + (look.coverSide === 'right' ? ' right' : '') + (s.playing ? ' playing' : '')
              + (look.coverShape === 'circle' ? ' circle' : '') + (look.spin ? ' spin' : '') + (look.align === 'center' ? ' center' : '') + (look.glow ? '' : ' noglow');
          }
          function progress() {
            if (!state || !state.visible || !(state.duration > 0)) return;
            var at = state.position + (state.playing ? (Date.now() - fetched) / 1000 : 0);
            at = Math.min(Math.max(at, 0), state.duration);
            document.getElementById('elapsed').textContent = clock(at);
            fill.style.width = (at / state.duration * 100) + '%';
          }
          async function tick() {
            try {
              var s = await (await fetch('/now-playing/state', { cache: 'no-store' })).json();
              state = s; fetched = Date.now();
              if (s.visible) paint(s); else card.className = card.className.replace('show', '').trim();
            } catch (e) { card.className = card.className.replace('show', '').trim(); }
          }
          tick(); setInterval(tick, 1000); setInterval(progress, 100);
        </script>
        </body>
        </html>
        """;
}
