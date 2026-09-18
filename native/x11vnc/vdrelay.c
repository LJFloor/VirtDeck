/*
 * vdrelay: run a program with a connected socket on its fd 0, and relay that socket to our own
 * stdin and stdout.
 *
 * VirtDeck runs `x11vnc -inetd` over an SSH exec channel so the RFB stream travels on the
 * channel's stdin and stdout. x11vnc's -inetd mode does `dup(0)` and then both reads and writes
 * that one descriptor, which is right for inetd (fd 0 is a socket there) and wrong for sshd, which
 * hands a command without a terminal two one-way pipes: the replies would go to a read-only pipe.
 * So this makes a connected pair of sockets, gives the program one end as fd 0, and pumps the other
 * end to and from the pipes. It is the whole of the fix, and it keeps x11vnc itself unpatched.
 *
 * The pair is TCP over loopback, not a socketpair. x11vnc refuses a client whose address it cannot
 * name ("check_access: denying empty host IP address string"), and naming a Unix socket's peer is
 * something glibc's getnameinfo does and musl's, which this is linked against, does not. A
 * loopback connection's peer is plainly 127.0.0.1. The listener lives only between listen() and
 * accept(), and the connection accepted must come from our own connecting socket's port, so a
 * process that races for the port gets refused rather than the viewer's stream.
 *
 *   vdrelay /path/to/program [args...]
 *
 * The program's stdout goes to /dev/null (x11vnc closes it anyway in -inetd mode) and its stderr
 * is inherited, so its log reaches the channel's stderr. Our stdin reaching EOF shuts the write
 * half of the socket, which is how the program learns the viewer is gone; the program closing the
 * socket, or our stdout going away, ends the relay. We then give the program a moment to exit on
 * its own, and exit with its status.
 *
 * Signals are never passed on. x11vnc caught mid-session by a SIGTERM can wedge in its own
 * handler and stay behind holding the X connection, while the viewer leaving (an EOF on its
 * socket) is a path it takes cleanly. So the program runs in a process group of its own, out of
 * reach of a signal sshd sends to ours; a signal to the relay ends the stream instead, exactly as
 * the viewer leaving would; and a program that is still there five seconds after its stream ended
 * gets SIGKILL, which cannot wedge anything.
 *
 * Part of VirtDeck; built statically by build-in-alpine.sh alongside x11vnc.
 */
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <arpa/inet.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <sys/socket.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

static pid_t child = -1;
static volatile sig_atomic_t stop = 0;

static void on_signal(int sig)
{
	(void)sig;
	stop = 1;
}

static int write_all(int fd, const char *p, size_t n)
{
	while (n > 0) {
		ssize_t w = write(fd, p, n);
		if (w < 0) {
			if (errno == EINTR && !stop)
				continue;
			return -1;
		}
		p += w;
		n -= (size_t)w;
	}
	return 0;
}

static void sleep_ms(long ms)
{
	struct timespec ts = { ms / 1000, (ms % 1000) * 1000000L };
	while (nanosleep(&ts, &ts) < 0 && errno == EINTR)
		;
}

/*
 * A connected pair of TCP sockets on 127.0.0.1: sv[0] for the relay, sv[1] for the program. See
 * the top of the file for why not socketpair(). Both close on exec; dup2 onto fd 0 clears that for
 * the one the program keeps.
 */
static int loopback_pair(int sv[2])
{
	struct sockaddr_in addr, mine;
	socklen_t len = sizeof addr, mine_len = sizeof mine;
	int one = 1;
	int l, c = -1, s = -1;

	l = socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC, 0);
	if (l < 0)
		return -1;
	memset(&addr, 0, sizeof addr);
	addr.sin_family = AF_INET;
	addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
	addr.sin_port = 0;
	if (bind(l, (struct sockaddr *)&addr, sizeof addr) < 0 || listen(l, 4) < 0 ||
	    getsockname(l, (struct sockaddr *)&addr, &len) < 0)
		goto fail;

	c = socket(AF_INET, SOCK_STREAM | SOCK_CLOEXEC, 0);
	if (c < 0 || connect(c, (struct sockaddr *)&addr, sizeof addr) < 0 ||
	    getsockname(c, (struct sockaddr *)&mine, &mine_len) < 0)
		goto fail;

	for (int tries = 0; tries < 16 && s < 0; tries++) {
		struct sockaddr_in peer;
		socklen_t peer_len = sizeof peer;
		s = accept4(l, (struct sockaddr *)&peer, &peer_len, SOCK_CLOEXEC);
		if (s < 0) {
			if (errno == EINTR)
				continue;
			goto fail;
		}
		if (peer.sin_port != mine.sin_port || peer.sin_addr.s_addr != mine.sin_addr.s_addr) {
			close(s); /* somebody else's connection: not the viewer's stream */
			s = -1;
		}
	}
	if (s < 0)
		goto fail;
	close(l);

	setsockopt(c, IPPROTO_TCP, TCP_NODELAY, &one, sizeof one);
	setsockopt(s, IPPROTO_TCP, TCP_NODELAY, &one, sizeof one);
	sv[0] = c;
	sv[1] = s;
	return 0;

fail:
	if (s >= 0)
		close(s);
	if (c >= 0)
		close(c);
	close(l);
	return -1;
}

/* Waits for the child to leave on its own, then makes it. */
static int reap(void)
{
	int status = 0;
	for (int i = 0; i < 50; i++) {
		pid_t r = waitpid(child, &status, WNOHANG);
		if (r == child)
			goto done;
		if (r < 0 && errno != EINTR)
			return 1;
		sleep_ms(100);
	}
	kill(child, SIGKILL); /* 5 s after the stream ended; see the top of the file for why not TERM */
	while (waitpid(child, &status, 0) < 0 && errno == EINTR)
		;
done:
	if (WIFEXITED(status))
		return WEXITSTATUS(status);
	if (WIFSIGNALED(status))
		return 128 + WTERMSIG(status);
	return 1;
}

int main(int argc, char **argv)
{
	int sv[2];
	static char buf[65536];

	if (argc < 2) {
		fprintf(stderr, "usage: vdrelay program [args...]\n");
		return 2;
	}

	if (loopback_pair(sv) < 0) {
		fprintf(stderr, "vdrelay: cannot make a loopback connection: %s\n", strerror(errno));
		return 111;
	}

	/* A write to a gone viewer must be an error we see, not a signal that kills the relay. */
	signal(SIGPIPE, SIG_IGN);

	child = fork();
	if (child < 0) {
		fprintf(stderr, "vdrelay: fork: %s\n", strerror(errno));
		return 111;
	}

	if (child == 0) {
		int nul;
		setpgid(0, 0); /* out of reach of a signal sent to the relay's group */
		close(sv[0]);
		if (dup2(sv[1], 0) < 0)
			_exit(111);
		nul = open("/dev/null", O_WRONLY);
		if (nul >= 0) {
			dup2(nul, 1);
			if (nul > 1)
				close(nul);
		}
		if (sv[1] > 1)
			close(sv[1]);
		signal(SIGPIPE, SIG_DFL);
		execv(argv[1], argv + 1);
		fprintf(stderr, "vdrelay: cannot run %s: %s\n", argv[1], strerror(errno));
		_exit(127);
	}

	close(sv[1]);

	{
		struct sigaction sa;
		memset(&sa, 0, sizeof sa);
		sa.sa_handler = on_signal; /* no SA_RESTART: poll must wake up and see it */
		sigaction(SIGTERM, &sa, NULL);
		sigaction(SIGHUP, &sa, NULL);
		sigaction(SIGINT, &sa, NULL);
	}

	{
		struct pollfd p[2];
		int in_open = 1;

		for (;;) {
			int r;
			p[0].fd = in_open ? 0 : -1;
			p[0].events = POLLIN;
			p[0].revents = 0;
			p[1].fd = sv[0];
			p[1].events = POLLIN;
			p[1].revents = 0;

			if (stop)
				break; /* told to go: end the stream, as the viewer leaving would */
			/* A timeout, so a signal landing just before the poll is still seen within a second. */
			r = poll(p, 2, 1000);
			if (r == 0)
				continue;
			if (r < 0) {
				if (errno == EINTR)
					continue;
				break;
			}

			if (in_open && (p[0].revents & (POLLIN | POLLHUP | POLLERR))) {
				ssize_t n = read(0, buf, sizeof buf);
				if (n > 0) {
					if (write_all(sv[0], buf, (size_t)n) < 0)
						break; /* the program closed its end */
				} else if (n == 0 || (errno != EINTR && errno != EAGAIN)) {
					/* The viewer is gone: tell the program by EOF, keep draining. */
					in_open = 0;
					shutdown(sv[0], SHUT_WR);
				}
			}

			if (p[1].revents & (POLLIN | POLLHUP | POLLERR)) {
				ssize_t n = read(sv[0], buf, sizeof buf);
				if (n > 0) {
					if (write_all(1, buf, (size_t)n) < 0)
						break; /* stdout is gone, so is the viewer */
				} else if (n == 0 || (errno != EINTR && errno != EAGAIN)) {
					break; /* the program closed the socket */
				}
			}
		}
	}

	close(sv[0]);
	close(0);
	close(1);
	return reap();
}
