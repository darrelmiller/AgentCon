# AgentCon wine supplier: one image for every seller; inventory, SLIM identity and model come from configuration.
#
#   docker build -t agentcon-supplier SupplierAgent
#
# The binary bundles the GitHub Copilot CLI runtime (github-copilot-sdk `bundled-cli`), which it extracts into
# SUPPLIER_COPILOT_STATE at first use, so the image needs no Node.js. That directory must be writable.

FROM rust:1.94-bookworm AS build
WORKDIR /build
# Dependencies first, so source edits reuse the cached dependency layer.
COPY Cargo.toml Cargo.lock ./
RUN mkdir src && echo 'fn main() {}' > src/main.rs && echo '' > src/lib.rs \
    && cargo build --release --locked \
    && rm -rf src
COPY schemas schemas
COPY src src
RUN touch src/main.rs src/lib.rs && cargo build --release --locked

FROM debian:bookworm-slim
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --system --uid 10001 --home-dir /var/lib/agentcon --create-home supplier
COPY --from=build /build/target/release/agentcon-supplier /usr/local/bin/agentcon-supplier
COPY inventories /opt/agentcon/inventories
USER 10001
ENV SUPPLIER_PORT=6000 \
    SUPPLIER_COPILOT_STATE=/var/lib/agentcon/copilot \
    SUPPLIER_INVENTORY=/opt/agentcon/inventories/barolo.json
# The HTTP interface listens on loopback only; probe it with the binary itself.
HEALTHCHECK --interval=15s --timeout=5s CMD ["agentcon-supplier", "ready-probe"]
ENTRYPOINT ["agentcon-supplier"]
