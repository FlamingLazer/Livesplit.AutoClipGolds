"""Local WebSocket 5 protocol fixture, using only the Python standard library."""
import base64, hashlib, json, socket, struct, os

def exact(conn, size):
    data = b''
    while len(data) < size:
        part = conn.recv(size-len(data))
        if not part: raise EOFError()
        data += part
    return data

def receive(conn):
    head = exact(conn, 2)
    length = head[1] & 127
    if length == 126: length = struct.unpack('!H', exact(conn, 2))[0]
    if length == 127: length = struct.unpack('!Q', exact(conn, 8))[0]
    mask = exact(conn, 4) if head[1] & 128 else None
    data = exact(conn, length)
    if mask: data = bytes(v ^ mask[i % 4] for i, v in enumerate(data))
    return json.loads(data)

def send(conn, value):
    data = json.dumps(value).encode()
    conn.sendall(b'\x81' + (bytes([len(data)]) if len(data) < 126 else b'\x7e' + struct.pack('!H', len(data))) + data)

with socket.socket() as server:
    server.bind(('127.0.0.1', 4456)); server.listen()
    for session in range(5):
        conn, _ = server.accept()
        with conn:
            conn.settimeout(10)
            request = b''
            while b'\r\n\r\n' not in request: request += conn.recv(4096)
            headers = dict(line.split(': ', 1) for line in request.decode().split('\r\n')[1:] if ': ' in line)
            key = next(v for k, v in headers.items() if k.lower() == 'sec-websocket-key')
            accept = base64.b64encode(hashlib.sha1((key+'258EAFA5-E914-47DA-95CA-C5AB0DC85B11').encode()).digest()).decode()
            conn.sendall(('HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: '+accept+'\r\n\r\n').encode())
            send(conn, {'op': 0, 'd': {'rpcVersion': 1, 'authentication': {'salt': 'salt', 'challenge': 'challenge'}}})
            identify = receive(conn)
            digest = lambda x: base64.b64encode(hashlib.sha256(x.encode()).digest()).decode()
            assert identify['d']['authentication'] == digest(digest('test-passwordsalt')+'challenge')
            send(conn, {'op': 2, 'd': {'negotiatedRpcVersion': 1}})
            for index in range(2):
                req = receive(conn)['d']
                data = {'outputActive': True}
                if req['requestType'] == 'SaveReplayBuffer':
                    # Intentionally deliver the event before the response.
                    path = os.path.abspath('work/GoldClip/fixture%d.mkv' % session) if session >= 2 else 'fixture.mkv'
                    send(conn, {'op': 5, 'd': {'eventType': 'ReplayBufferSaved', 'eventData': {'savedReplayPath': path}}})
                    data = {}
                send(conn, {'op': 7, 'd': {'requestId': req['requestId'], 'requestStatus': {'result': True, 'code': 100}, 'responseData': data}})
    print('PASS: five authenticated sessions and early save events', flush=True)
