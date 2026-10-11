import AVFoundation

extension CameraSession {
    func readControls(completion: @escaping (CameraControlState) -> Void) {
        sessionQueue.async {
            let state = self.controlSnapshot()
            DispatchQueue.main.async { completion(state) }
        }
    }
    func turnTorchOff() {
        sessionQueue.async { if let device = self.currentDevice { self.disableTorch(device) } }
    }
    func disableTorch(_ device: AVCaptureDevice) {
        guard device.hasTorch, device.isTorchModeSupported(.off) else { return }
        do { try device.lockForConfiguration(); device.torchMode = .off; device.unlockForConfiguration() }
        catch { DiagnosticLog.write("Torch shutdown: \(error.localizedDescription)") }
    }
    private func controlSnapshot() -> CameraControlState {
        var state = CameraControlState()
        guard let d = currentDevice else { return state }
        state.camera_id = d.uniqueID
        state.torch_available = d.hasTorch && d.isTorchAvailable && d.isTorchModeSupported(.on)
        state.torch_on = d.isTorchActive
        state.auto_exposure_supported = d.isExposureModeSupported(.continuousAutoExposure)
        state.auto_focus_supported = d.isFocusModeSupported(.continuousAutoFocus)
        state.auto_white_balance_supported = d.isWhiteBalanceModeSupported(.continuousAutoWhiteBalance)
        if state.auto_exposure_supported {
            state.ranges["exposure_bias"] = .init(min: Double(d.minExposureTargetBias), max: Double(d.maxExposureTargetBias))
        }
        if d.isExposureModeSupported(.custom) {
            state.ranges["iso"] = .init(min: Double(d.activeFormat.minISO), max: Double(d.activeFormat.maxISO))
            let frame = CMTimeGetSeconds(d.activeVideoMinFrameDuration)
            let minMs = CMTimeGetSeconds(d.activeFormat.minExposureDuration) * 1000
            let maxMs = min(CMTimeGetSeconds(d.activeFormat.maxExposureDuration), frame) * 1000
            state.ranges["shutter_ms"] = .init(min: minMs, max: max(minMs, maxMs))
        }
        if d.isLockingFocusWithCustomLensPositionSupported { state.ranges["focus"] = .init(min: 0, max: 1) }
        if d.isWhiteBalanceModeSupported(.locked) {
            state.ranges["temperature"] = .init(min: 2500, max: 8000)
            state.ranges["tint"] = .init(min: -150, max: 150)
        }
        if state.torch_available { state.ranges["torch_level"] = .init(min: 0.01, max: Double(AVCaptureDevice.maxAvailableTorchLevel)) }
        let balance = d.temperatureAndTintValues(for: d.deviceWhiteBalanceGains)
        state.values = ["exposure_bias": Double(d.exposureTargetBias), "iso": Double(d.iso),
            "shutter_ms": CMTimeGetSeconds(d.exposureDuration) * 1000, "focus": Double(d.lensPosition),
            "temperature": Double(balance.temperature), "tint": Double(balance.tint), "torch_level": Double(d.torchLevel),
            "auto_exposure": d.exposureMode == .custom || d.exposureMode == .locked ? 0 : 1,
            "auto_focus": d.focusMode == .locked ? 0 : 1,
            "auto_white_balance": d.whiteBalanceMode == .locked ? 0 : 1]
        state.values = state.values.filter { $0.value.isFinite }
        return state
    }
    func applyControls(_ request: CameraControlRequest, allowTorch: Bool,
                       completion: @escaping (CameraControlState) -> Void) {
        sessionQueue.async {
            func finish(_ error: String? = nil) {
                var state = self.controlSnapshot()
                state.request_id = request.request_id; state.error = error
                DispatchQueue.main.async { completion(state) }
            }
            guard let d = self.currentDevice, request.camera_id == d.uniqueID else {
                finish("Camera changed. Wait for updated controls."); return
            }
            let snapshot = self.controlSnapshot()
            if let error = snapshot.validationError(for: request) { finish(error); return }
            let v = request.values
            let reset = v["reset"] == 1
            let autoExposure = reset ? true : (v["auto_exposure"].map { $0 == 1 } ?? (d.exposureMode != .custom))
            let autoFocus = reset ? true : (v["auto_focus"].map { $0 == 1 } ?? (d.focusMode != .locked))
            let autoBalance = reset ? true : (v["auto_white_balance"].map { $0 == 1 } ?? (d.whiteBalanceMode != .locked))
            if v["auto_exposure"] != nil && !(autoExposure ? snapshot.auto_exposure_supported : d.isExposureModeSupported(.custom)) { finish("Exposure mode unavailable."); return }
            if v["auto_focus"] != nil && !(autoFocus ? snapshot.auto_focus_supported : d.isLockingFocusWithCustomLensPositionSupported) { finish("Focus mode unavailable."); return }
            if v["auto_white_balance"] != nil && !(autoBalance ? snapshot.auto_white_balance_supported : d.isWhiteBalanceModeSupported(.locked)) { finish("White balance mode unavailable."); return }
            if v["torch_on"] == 1 && (!allowTorch || !snapshot.torch_available) { finish("Torch is available only while streaming with a supported camera."); return }
            let group = DispatchGroup()
            do {
                try d.lockForConfiguration()
                defer { d.unlockForConfiguration() }
                if reset || v["auto_exposure"] != nil || v["iso"] != nil || v["shutter_ms"] != nil {
                    if autoExposure {
                        if snapshot.auto_exposure_supported { d.exposureMode = .continuousAutoExposure }
                    } else {
                        let range = snapshot.ranges["shutter_ms"]!
                        let duration = min(max(v["shutter_ms"] ?? CMTimeGetSeconds(d.exposureDuration)*1000, range.min), range.max)
                        let iso = Float(v["iso"] ?? Double(d.iso))
                        group.enter()
                        d.setExposureModeCustom(duration: CMTime(seconds: duration/1000, preferredTimescale: 1_000_000), iso: iso) { _ in group.leave() }
                    }
                }
                if (reset || v["exposure_bias"] != nil), snapshot.ranges["exposure_bias"] != nil {
                    group.enter(); d.setExposureTargetBias(Float(reset ? 0 : v["exposure_bias"]!)) { _ in group.leave() }
                }
                if reset || v["auto_focus"] != nil || v["focus"] != nil {
                    if autoFocus { if snapshot.auto_focus_supported { d.focusMode = .continuousAutoFocus } }
                    else if d.isLockingFocusWithCustomLensPositionSupported {
                        group.enter(); d.setFocusModeLocked(lensPosition: Float(v["focus"] ?? Double(d.lensPosition))) { _ in group.leave() }
                    }
                }
                if reset || v["auto_white_balance"] != nil || v["temperature"] != nil || v["tint"] != nil {
                    if autoBalance { if snapshot.auto_white_balance_supported { d.whiteBalanceMode = .continuousAutoWhiteBalance } }
                    else {
                        let current = d.temperatureAndTintValues(for: d.deviceWhiteBalanceGains)
                        var gains = d.deviceWhiteBalanceGains(for: .init(temperature: Float(v["temperature"] ?? Double(current.temperature)), tint: Float(v["tint"] ?? Double(current.tint))))
                        gains.redGain = min(max(gains.redGain, 1), d.maxWhiteBalanceGain)
                        gains.greenGain = min(max(gains.greenGain, 1), d.maxWhiteBalanceGain)
                        gains.blueGain = min(max(gains.blueGain, 1), d.maxWhiteBalanceGain)
                        group.enter(); d.setWhiteBalanceModeLocked(with: gains) { _ in group.leave() }
                    }
                }
                if v["torch_on"] == 0 || reset { if d.hasTorch { d.torchMode = .off } }
                else if v["torch_on"] == 1 || (v["torch_level"] != nil && d.isTorchActive) {
                    guard allowTorch && snapshot.torch_available else { finish("Torch unavailable. The phone may be too warm."); return }
                    try d.setTorchModeOn(level: Float(v["torch_level"] ?? max(Double(d.torchLevel), 0.5)))
                }
            } catch { finish(error.localizedDescription); return }
            group.notify(queue: self.sessionQueue) { finish() }
        }
    }
}
