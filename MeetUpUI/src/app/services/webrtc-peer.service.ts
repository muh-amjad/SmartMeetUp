import { Injectable, Signal, signal } from '@angular/core';

export interface RemoteVideoItem {
  userId: string;
  mediaStream: MediaStream;
}

@Injectable({
  providedIn: 'root'
})
export class WebrtcPeerService {
  private peerConnections: Map<string, RTCPeerConnection> = new Map();
  private remoteStreams: Map<string, MediaStream> = new Map();
  private pendingIceCandidates: Map<string, RTCIceCandidate[]> = new Map();

  private remoteVideosSignal = signal<RemoteVideoItem[]>([]);
  public remoteVideos: Signal<RemoteVideoItem[]> = this.remoteVideosSignal.asReadonly();

  private localStreamSignal = signal<MediaStream | null>(null);
  public localStream: Signal<MediaStream | null> = this.localStreamSignal.asReadonly();

  private readonly rtcConfig: RTCConfiguration = {
    iceServers: [
      { urls: ['stun:stun.l.google.com:19302'] },
      { urls: ['stun:stun1.l.google.com:19302'] }
    ]
  };

  async getUserMedia(constraints: MediaStreamConstraints = { audio: true, video: true }): Promise<MediaStream> {
    const stream = await navigator.mediaDevices.getUserMedia(constraints);
    this.localStreamSignal.set(stream);
    return stream;
  }

  async getDisplayMedia(): Promise<MediaStream> {
    return navigator.mediaDevices.getDisplayMedia({ audio: false, video: true });
  }

  createPeerConnection(peerId: string): RTCPeerConnection {
    const pc = new RTCPeerConnection(this.rtcConfig);

    // Add local stream tracks
    const localStream = this.localStreamSignal();
    if (localStream) {
      localStream.getTracks().forEach(track => {
        pc.addTrack(track, localStream);
      });
    }

    // Handle remote stream
    pc.ontrack = (event) => {
      this.remoteStreams.set(peerId, event.streams[0]);
      this.updateRemoteVideos();
    };

    // Handle ICE candidates
    pc.onicecandidate = (event) => {
      if (event.candidate) {
        // Send candidate to peer
        this.onIceCandidate(peerId, event.candidate);
      }
    };

    // Store pending candidates for this peer
    if (!this.pendingIceCandidates.has(peerId)) {
      this.pendingIceCandidates.set(peerId, []);
    }

    this.peerConnections.set(peerId, pc);
    return pc;
  }

  async createOffer(peerId: string): Promise<RTCSessionDescriptionInit> {
    let pc = this.peerConnections.get(peerId);
    if (!pc) {
      pc = this.createPeerConnection(peerId);
    }

    const offer = await pc.createOffer();
    await pc.setLocalDescription(offer);
    return offer;
  }

  async createAnswer(peerId: string, offer: RTCSessionDescriptionInit): Promise<RTCSessionDescriptionInit> {
    let pc = this.peerConnections.get(peerId);
    if (!pc) {
      pc = this.createPeerConnection(peerId);
    }

    await pc.setRemoteDescription(new RTCSessionDescription(offer));
    const answer = await pc.createAnswer();
    await pc.setLocalDescription(answer);
    return answer;
  }

  async addAnswer(peerId: string, answer: RTCSessionDescriptionInit): Promise<void> {
    const pc = this.peerConnections.get(peerId);
    if (pc) {
      await pc.setRemoteDescription(new RTCSessionDescription(answer));
      await this.processPendingCandidates(peerId);
    }
  }

  addIceCandidate(peerId: string, candidate: RTCIceCandidateInit): void {
    const pc = this.peerConnections.get(peerId);
    if (pc && pc.remoteDescription) {
      pc.addIceCandidate(new RTCIceCandidate(candidate)).catch(e => {
        console.error('Error adding ICE candidate:', e);
      });
    } else {
      // Store for later
      const pending = this.pendingIceCandidates.get(peerId) || [];
      pending.push(new RTCIceCandidate(candidate));
      this.pendingIceCandidates.set(peerId, pending);
    }
  }

  closePeerConnection(peerId: string): void {
    const pc = this.peerConnections.get(peerId);
    if (pc) {
      pc.close();
      this.peerConnections.delete(peerId);
    }

    this.remoteStreams.delete(peerId);
    this.pendingIceCandidates.delete(peerId);
    this.updateRemoteVideos();
  }

  stopLocalStream(): void {
    const stream = this.localStreamSignal();
    if (stream) {
      stream.getTracks().forEach(track => track.stop());
      this.localStreamSignal.set(null);
    }
  }

  getRemoteStream(peerId: string): MediaStream | undefined {
    return this.remoteStreams.get(peerId);
  }

  private async processPendingCandidates(peerId: string): Promise<void> {
    const pending = this.pendingIceCandidates.get(peerId);
    if (pending) {
      const pc = this.peerConnections.get(peerId);
      if (pc) {
        for (const candidate of pending) {
          try {
            await pc.addIceCandidate(candidate);
          } catch (e) {
            console.error('Error adding pending ICE candidate:', e);
          }
        }
        this.pendingIceCandidates.set(peerId, []);
      }
    }
  }

  private updateRemoteVideos(): void {
    const videos = Array.from(this.remoteStreams.entries()).map(([userId, mediaStream]) => ({
      userId,
      mediaStream
    }));
    this.remoteVideosSignal.set(videos);
  }

  private onIceCandidate(peerId: string, candidate: RTCIceCandidate): void {
    // This will be called by the component to send to SignalR
    console.log('ICE candidate for', peerId, candidate);
  }
}
