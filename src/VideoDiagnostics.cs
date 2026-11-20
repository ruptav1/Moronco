namespace MiniumBrowser
{
    // Only invoked by --smoke=video. Normal browsing does not inject this script,
    // mute audio, change playback, or poll page internals.
    internal static class VideoDiagnostics
    {
        internal const string ProbeScript = @"(() => {
            const v = document.querySelector('video');
            const p = document.getElementById('movie_player');
            const err = document.querySelector('.ytp-error-content-wrap');
            // Diagnostics may press YouTube's normal Skip button once the
            // site makes it available; never click ad links or force a seek.
            if (p && p.classList.contains('ad-showing')) {
                const skip = p.querySelector('.ytp-skip-ad-button, .ytp-ad-skip-button');
                if (skip && skip.offsetWidth > 0 && !skip.disabled) {
                    skip.click();
                    window.__miniumSkippedAds = (window.__miniumSkippedAds || 0) + 1;
                }
            }
            const data = p && p.getVideoData ? p.getVideoData() : null;
            const base = {url:location.href, title:document.title, video:!!v,
                contentId:data ? data.video_id : null,
                skipButtonPresses:window.__miniumSkippedAds || 0,
                ad:!!(p && p.classList.contains('ad-showing')),
                error:err ? err.innerText : null,
                heapUsed:performance.memory ? performance.memory.usedJSHeapSize : null,
                bodyLength:document.body ? document.body.innerText.length : 0,
                imageCount:document.images.length,
                loadedImages:Array.from(document.images).filter(i => i.naturalWidth > 0).length};
            if (!v) return base;
            v.muted = true;
            if (v.paused && !v.ended) {
                const attempt = v.play();
                if (attempt) attempt.catch(e => { window.__miniumPlayError = e.name; });
            }
            const q = v.getVideoPlaybackQuality ? v.getVideoPlaybackQuality() : null;
            let bufferedSeconds = 0;
            for (let i=0; i<v.buffered.length; i++) bufferedSeconds += v.buffered.end(i)-v.buffered.start(i);
            return Object.assign(base, {time:v.currentTime, duration:v.duration,
                width:v.videoWidth, height:v.videoHeight, paused:v.paused,
                ready:v.readyState, ended:v.ended, playbackRate:v.playbackRate,
                totalFrames:q ? q.totalVideoFrames : null,
                droppedFrames:q ? q.droppedVideoFrames : null,
                bufferedSeconds:bufferedSeconds,
                playError:window.__miniumPlayError || null});
        })()";
    }
}
