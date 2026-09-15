// The WebView's own device APIs: nothing here goes through /_bridge. Each one only works in the app when it
// calls AllowWebPermissions for that permission.

let cameraStream = null;
let micStream = null;
let micContext = null;
let micTimer = 0;

const describe = e => `${e.name || "Error"}: ${e.message || e}`;

export async function startCamera(video) {
    stopCamera(video);

    try {
        cameraStream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: "environment" }, audio: false });
        video.srcObject = cameraStream;
        await video.play();
        const settings = cameraStream.getVideoTracks()[0].getSettings();
        return `camera ${settings.width}×${settings.height}`;
    } catch (e) {
        throw new Error(describe(e));
    }
}

export function stopCamera(video) {
    cameraStream?.getTracks().forEach(t => t.stop());
    cameraStream = null;

    if (video)
        video.srcObject = null;
}

export async function startMicrophone(listener) {
    stopMicrophone();

    try {
        micStream = await navigator.mediaDevices.getUserMedia({ audio: true, video: false });
    } catch (e) {
        throw new Error(describe(e));
    }

    micContext = new AudioContext();
    const analyser = micContext.createAnalyser();
    analyser.fftSize = 512;
    micContext.createMediaStreamSource(micStream).connect(analyser);

    const samples = new Uint8Array(analyser.fftSize);

    // A few updates a second is plenty for a meter, and keeps interop calls cheap.
    micTimer = setInterval(() => {
        analyser.getByteTimeDomainData(samples);
        let peak = 0;
        for (const s of samples)
            peak = Math.max(peak, Math.abs(s - 128));

        listener.invokeMethodAsync("OnLevel", Math.round(peak / 128 * 100));
    }, 150);
}

export function stopMicrophone() {
    clearInterval(micTimer);
    micTimer = 0;
    micStream?.getTracks().forEach(t => t.stop());
    micStream = null;
    micContext?.close();
    micContext = null;
}

export function getPosition() {
    return new Promise((resolve, reject) => {
        if (!navigator.geolocation) {
            reject(new Error("navigator.geolocation is not available"));
            return;
        }

        navigator.geolocation.getCurrentPosition(
            p => resolve({ latitude: p.coords.latitude, longitude: p.coords.longitude, accuracy: p.coords.accuracy }),
            e => reject(new Error(`geolocation error ${e.code}: ${e.message}`)),
            { enableHighAccuracy: true, timeout: 15000 }
        );
    });
}
