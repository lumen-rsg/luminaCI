# Include inside the host nginx HTTPS server block for
# packages.lumina.1t.ru. The bundled Lumina nginx listens on loopback port
# 18080 when docker-compose.host-nginx.yml is active.

add_header Referrer-Policy "strict-origin-when-cross-origin" always;
add_header Content-Security-Policy "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'self'; base-uri 'self'; form-action 'none';" always;
add_header Permissions-Policy "camera=(), microphone=(), geolocation=()" always;

location = / {
    proxy_pass http://127.0.0.1:18080;
    proxy_set_header Host packages.lumina.1t.ru;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto https;
    proxy_connect_timeout 5s;
    proxy_read_timeout 30s;
}

location ^~ /assets/ {
    proxy_pass http://127.0.0.1:18080;
    proxy_set_header Host packages.lumina.1t.ru;
    expires 1y;
}

location = /favicon.svg {
    proxy_pass http://127.0.0.1:18080;
    proxy_set_header Host packages.lumina.1t.ru;
}

location = /site.webmanifest {
    proxy_pass http://127.0.0.1:18080;
    proxy_set_header Host packages.lumina.1t.ru;
}

location = /lumina.repo {
    proxy_pass http://127.0.0.1:18080;
    proxy_set_header Host packages.lumina.1t.ru;
    default_type text/plain;
}

location = /RPM-GPG-KEY-lumina {
    proxy_pass http://127.0.0.1:18080;
    proxy_set_header Host packages.lumina.1t.ru;
    default_type application/pgp-keys;
}

location = /api/package-index {
    return 308 /api/package-index/;
}

# Read-only machine index for the public package browser. It exposes only the
# same repository tree already downloadable under /lumen/, never staging or
# service state.
location /api/package-index/ {
    limit_except GET { deny all; }
    alias /home/mir1tlinux/lumina-ci-repository-data/var/lib/lumina/repositories/;
    autoindex on;
    autoindex_format json;
    autoindex_exact_size on;
    autoindex_localtime off;
}
