import { TestBed } from '@angular/core/testing';
import { MeetingMediaService } from './meeting-media.service';

type MockTrack = {
  kind: 'video' | 'audio';
  enabled: boolean;
  stopCalled: boolean;
  stop: () => void;
};

type MockStream = {
  tracks: MockTrack[];
  getTracks: () => MockTrack[];
  getVideoTracks: () => MockTrack[];
  getAudioTracks: () => MockTrack[];
  addTrack: (track: MockTrack) => void;
  removeTrack: (track: MockTrack) => void;
};

describe('MeetingMediaService', () => {
  let service: MeetingMediaService;

  function createTrack(kind: 'video' | 'audio'): MockTrack {
    return {
      kind,
      enabled: true,
      stopCalled: false,
      stop() {
        this.stopCalled = true;
      },
    };
  }

  function createStream(initialTracks: MockTrack[]): MediaStream {
    const stream: MockStream = {
      tracks: [...initialTracks],
      getTracks() {
        return this.tracks;
      },
      getVideoTracks() {
        return this.tracks.filter((t) => t.kind === 'video');
      },
      getAudioTracks() {
        return this.tracks.filter((t) => t.kind === 'audio');
      },
      addTrack(track) {
        this.tracks.push(track);
      },
      removeTrack(track) {
        this.tracks = this.tracks.filter((t) => t !== track);
      },
    };

    return stream as unknown as MediaStream;
  }

  function mockGetUserMedia(impl: (constraints: MediaStreamConstraints) => Promise<MediaStream>) {
    Object.defineProperty(navigator, 'mediaDevices', {
      configurable: true,
      value: { getUserMedia: impl },
    });
  }

  beforeEach(() => {
    TestBed.configureTestingModule({});
    service = TestBed.inject(MeetingMediaService);
  });

  it('preserves toggle intent before a stream is created', async () => {
    await service.toggleCamera();
    await service.toggleMic();

    expect(service.isCameraOn()).toBe(true);
    expect(service.isMicOn()).toBe(true);
  });

  it('marks camera and mic as on once the stream is actually acquired', async () => {
    const videoTrack = createTrack('video');
    const audioTrack = createTrack('audio');
    const stream = createStream([videoTrack, audioTrack]);
    mockGetUserMedia(async () => stream);

    const createdStream = await service.ensureLocalStream();

    expect(createdStream).toBe(stream);
    expect(service.isCameraOn()).toBe(true);
    expect(service.isMicOn()).toBe(true);
  });

  it('stops and removes the hardware track when the camera is turned off', async () => {
    const videoTrack = createTrack('video');
    const audioTrack = createTrack('audio');
    const stream = createStream([videoTrack, audioTrack]);
    mockGetUserMedia(async () => stream);

    await service.ensureLocalStream();
    await service.toggleCamera();

    expect(videoTrack.stopCalled).toBe(true);
    expect(service.isCameraOn()).toBe(false);
    expect((stream.getVideoTracks() as unknown as MockTrack[]).length).toBe(0);
    // Mic must be untouched by a camera-only toggle.
    expect(audioTrack.stopCalled).toBe(false);
  });

  it('stops and removes the hardware track when the mic is turned off', async () => {
    const videoTrack = createTrack('video');
    const audioTrack = createTrack('audio');
    const stream = createStream([videoTrack, audioTrack]);
    mockGetUserMedia(async () => stream);

    await service.ensureLocalStream();
    await service.toggleMic();

    expect(audioTrack.stopCalled).toBe(true);
    expect(service.isMicOn()).toBe(false);
    expect((stream.getAudioTracks() as unknown as MockTrack[]).length).toBe(0);
    expect(videoTrack.stopCalled).toBe(false);
  });

  it('re-acquires a fresh track when the camera is turned back on', async () => {
    const videoTrack = createTrack('video');
    const audioTrack = createTrack('audio');
    const stream = createStream([videoTrack, audioTrack]);

    let calls = 0;
    mockGetUserMedia(async (constraints) => {
      calls += 1;
      if (constraints.video && !constraints.audio) {
        // camera-only re-acquire after toggling back on
        return createStream([createTrack('video')]);
      }
      return stream;
    });

    await service.ensureLocalStream();
    await service.toggleCamera(); // off — stops the original track
    await service.toggleCamera(); // on — must request a brand new one

    expect(calls).toBe(2); // initial combined acquire + camera re-acquire
    expect(service.isCameraOn()).toBe(true);
    expect((stream.getVideoTracks() as unknown as MockTrack[]).length).toBe(1);
    expect((stream.getVideoTracks() as unknown as MockTrack[])[0]).not.toBe(videoTrack);
  });

  it('stops every remaining track on stopStream and clears the stream', async () => {
    const videoTrack = createTrack('video');
    const audioTrack = createTrack('audio');
    const stream = createStream([videoTrack, audioTrack]);
    mockGetUserMedia(async () => stream);

    await service.ensureLocalStream();
    service.stopStream();

    expect(videoTrack.stopCalled).toBe(true);
    expect(audioTrack.stopCalled).toBe(true);
    expect(service.localStream()).toBeNull();
  });
});
