import { TestBed } from '@angular/core/testing';
import { MeetingMediaService } from './meeting-media.service';

type MockTrack = {
  enabled: boolean;
  stop: () => void;
  stopCalled: boolean;
};

type MockStream = {
  getTracks: () => MockTrack[];
  getVideoTracks: () => MockTrack[];
  getAudioTracks: () => MockTrack[];
};

describe('MeetingMediaService', () => {
  let service: MeetingMediaService;

  function createTrack(): MockTrack {
    return {
      enabled: true,
      stopCalled: false,
      stop() {
        this.stopCalled = true;
      },
    };
  }

  function createStream(videoTrack: MockTrack, audioTrack: MockTrack): MediaStream {
    const stream: MockStream = {
      getTracks: () => [videoTrack, audioTrack],
      getVideoTracks: () => [videoTrack],
      getAudioTracks: () => [audioTrack],
    };

    return stream as unknown as MediaStream;
  }

  beforeEach(() => {
    TestBed.configureTestingModule({});
    service = TestBed.inject(MeetingMediaService);
  });

  it('should preserve toggle intent before stream is created', async () => {
    service.toggleCamera();
    service.toggleMic();

    expect(service.isCameraOn()).toBe(true);
    expect(service.isMicOn()).toBe(true);
  });

  it('should apply stored camera and mic state to new stream tracks', async () => {
    const videoTrack = createTrack();
    const audioTrack = createTrack();
    const stream = createStream(videoTrack, audioTrack);

    service.toggleCamera();
    service.toggleMic();

    let getUserMediaCalls = 0;
    Object.defineProperty(navigator, 'mediaDevices', {
      configurable: true,
      value: {
        async getUserMedia() {
          getUserMediaCalls += 1;
          return stream;
        },
      },
    });

    const createdStream = await service.ensureLocalStream();

    expect(createdStream).toBe(stream);
    expect(getUserMediaCalls).toBe(1);
    expect(videoTrack.enabled).toBe(true);
    expect(audioTrack.enabled).toBe(true);
  });

  it('should not reset camera and mic toggle state on stopStream', async () => {
    const videoTrack = createTrack();
    const audioTrack = createTrack();
    const stream = createStream(videoTrack, audioTrack);

    Object.defineProperty(navigator, 'mediaDevices', {
      configurable: true,
      value: {
        async getUserMedia() {
          return stream;
        },
      },
    });

    await service.ensureLocalStream();
    service.toggleCamera();
    service.toggleMic();

    expect(service.isCameraOn()).toBe(true);
    expect(service.isMicOn()).toBe(true);

    service.stopStream();

    expect(videoTrack.stopCalled).toBe(true);
    expect(audioTrack.stopCalled).toBe(true);
    expect(service.localStream()).toBeNull();
    expect(service.isCameraOn()).toBe(true);
    expect(service.isMicOn()).toBe(true);
  });

  it('should update existing tracks when toggling during active stream', async () => {
    const videoTrack = createTrack();
    const audioTrack = createTrack();
    const stream = createStream(videoTrack, audioTrack);

    Object.defineProperty(navigator, 'mediaDevices', {
      configurable: true,
      value: {
        async getUserMedia() {
          return stream;
        },
      },
    });

    await service.ensureLocalStream();

    service.toggleCamera();
    service.toggleMic();

    expect(videoTrack.enabled).toBe(true);
    expect(audioTrack.enabled).toBe(true);

    service.toggleCamera();
    service.toggleMic();

    expect(videoTrack.enabled).toBe(false);
    expect(audioTrack.enabled).toBe(false);
  });
});
