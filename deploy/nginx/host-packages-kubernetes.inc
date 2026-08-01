# Include these locations inside the host nginx HTTPS server block for
# packages.lumina.1t.ru. The bundled Lumina nginx listens on loopback port
# 18080 when docker-compose.host-nginx.yml is active.

location ^~ /fedora/ {
    limit_except GET { deny all; }
    proxy_pass http://127.0.0.1:18080;
    proxy_set_header Host packages.lumina.1t.ru;
    proxy_connect_timeout 15s;
    proxy_read_timeout 300s;
}

location ^~ /lumina-sources/ {
    limit_except GET { deny all; }
    proxy_pass http://127.0.0.1:18080;
    proxy_set_header Host packages.lumina.1t.ru;
    proxy_request_buffering off;
    proxy_buffering off;
    proxy_connect_timeout 15s;
    proxy_read_timeout 1800s;
}

location ^~ /lumina-artifacts/ {
    limit_except GET PUT { deny all; }
    client_max_body_size 0;
    proxy_pass http://127.0.0.1:18080;
    proxy_set_header Host packages.lumina.1t.ru;
    proxy_request_buffering off;
    proxy_buffering off;
    proxy_connect_timeout 15s;
    proxy_send_timeout 1800s;
    proxy_read_timeout 1800s;
}
