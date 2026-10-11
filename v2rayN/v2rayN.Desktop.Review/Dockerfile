FROM ubuntu:24.04
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
    xvfb xauth libfontconfig1 libfreetype6 libx11-6 libx11-xcb1 libxcb1 libice6 libsm6 \
    libglib2.0-0 libicu74 libssl3t64 libgomp1 fonts-noto-cjk \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
ENTRYPOINT ["sh", "-c", "Xvfb :99 -ac -screen 0 1920x1200x24 >/tmp/xvfb.log 2>&1 & sleep 1; export DISPLAY=:99; exec /app/v2rayN.Desktop.Review \"$@\"", "ui-review"]
