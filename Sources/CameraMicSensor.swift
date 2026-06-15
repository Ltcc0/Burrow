//
//  CameraMicSensor.swift
//  Burrow
//
//  Honest, passive camera/microphone in-use detection for the menu-bar
//  popover — the same "is some process using this device" signal the macOS
//  amber dot reflects. Reads CoreMediaIO's kCMIODevicePropertyDeviceIsRunning-
//  Somewhere (camera) and CoreAudio's kAudioDevicePropertyDeviceIsRunning-
//  Somewhere (mic). These are passive property reads — Burrow never opens an
//  AVCaptureSession, so there is no TCC prompt and no NSCamera/Microphone
//  usage description needed, and Burrow itself never lights the dot.
//
//  It reports system-level "in use" (matching Control Center), so it will
//  also light for Siri / dictation / Continuity Camera — labelled neutrally
//  as "in use", never faked into per-app attribution. Opt-in (off by default).
//

import Foundation
import CoreMediaIO
import CoreAudio

enum CameraMicSensor {

    /// Whether any camera device reports it's running somewhere.
    static func cameraInUse() -> Bool {
        var addr = CMIOObjectPropertyAddress(
            mSelector: CMIOObjectPropertySelector(kCMIOHardwarePropertyDevices),
            mScope: CMIOObjectPropertyScope(kCMIOObjectPropertyScopeGlobal),
            mElement: CMIOObjectPropertyElement(0))   // main element
        var dataSize: UInt32 = 0
        let sys = CMIOObjectID(kCMIOObjectSystemObject)
        guard CMIOObjectGetPropertyDataSize(sys, &addr, 0, nil, &dataSize) == OSStatus(0),
              dataSize > 0 else { return false }
        let count = Int(dataSize) / MemoryLayout<CMIOObjectID>.size
        var devices = [CMIOObjectID](repeating: 0, count: count)
        var used: UInt32 = 0
        guard CMIOObjectGetPropertyData(sys, &addr, 0, nil, dataSize, &used, &devices) == OSStatus(0)
        else { return false }

        var runAddr = CMIOObjectPropertyAddress(
            mSelector: CMIOObjectPropertySelector(kCMIODevicePropertyDeviceIsRunningSomewhere),
            mScope: CMIOObjectPropertyScope(kCMIOObjectPropertyScopeGlobal),
            mElement: CMIOObjectPropertyElement(0))
        for device in devices where device != 0 {
            var running: UInt32 = 0
            var size = UInt32(MemoryLayout<UInt32>.size)
            if CMIOObjectGetPropertyData(device, &runAddr, 0, nil, size, &size, &running) == OSStatus(0),
               running != 0 { return true }
        }
        return false
    }

    /// Whether any input (microphone) device reports it's running somewhere.
    static func micInUse() -> Bool {
        var addr = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDevices,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        var dataSize: UInt32 = 0
        let sys = AudioObjectID(kAudioObjectSystemObject)
        guard AudioObjectGetPropertyDataSize(sys, &addr, 0, nil, &dataSize) == noErr,
              dataSize > 0 else { return false }
        let count = Int(dataSize) / MemoryLayout<AudioObjectID>.size
        var devices = [AudioObjectID](repeating: 0, count: count)
        guard AudioObjectGetPropertyData(sys, &addr, 0, nil, &dataSize, &devices) == noErr
        else { return false }

        for device in devices where device != 0 {
            // Only consider devices that actually have input streams — a
            // playback-only device "running" isn't the microphone.
            var streamAddr = AudioObjectPropertyAddress(
                mSelector: kAudioDevicePropertyStreams,
                mScope: kAudioObjectPropertyScopeInput,
                mElement: kAudioObjectPropertyElementMain)
            var streamSize: UInt32 = 0
            guard AudioObjectGetPropertyDataSize(device, &streamAddr, 0, nil, &streamSize) == noErr,
                  streamSize > 0 else { continue }

            var runAddr = AudioObjectPropertyAddress(
                mSelector: kAudioDevicePropertyDeviceIsRunningSomewhere,
                mScope: kAudioObjectPropertyScopeGlobal,
                mElement: kAudioObjectPropertyElementMain)
            var running: UInt32 = 0
            var size = UInt32(MemoryLayout<UInt32>.size)
            if AudioObjectGetPropertyData(device, &runAddr, 0, nil, &size, &running) == noErr,
               running != 0 { return true }
        }
        return false
    }
}
