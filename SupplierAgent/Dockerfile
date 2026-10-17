# AgentCon wine supplier (seller agent): one generic image for every seller, configured by input files:
# SUPPLIER_INVENTORY (stock), SUPPLIER_SLIM_CONFIG (SLIM node and identity), MODEL_* (OpenAI-compatible model
# endpoint). Built without the GitHub Copilot interpreter (`--no-default-features`), which is for laptops.
#
#   docker build -t agentcon-supplier SupplierAgent

FROM rust:1.94-bookworm AS build
# Build-time only; none of this reaches the runtime image:
# - cmake and libclang: aws-lc-sys (crypto for rustls and SLIM's MLS) builds AWS-LC from C, and SLIM's crates
#   enable its `bindgen` feature, which needs libclang.
# - libssl-dev and pkg-config: a2a-rs pulls in reqwest's native-tls, which links OpenSSL.
RUN apt-get update \
    && apt-get install -y --no-install-recommends cmake clang libclang-dev libssl-dev pkg-config \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /build
# Dependencies first, so source edits reuse the cached dependency layer.
COPY Cargo.toml Cargo.lock ./
RUN mkdir src && echo 'fn main() {}' > src/main.rs && echo '' > src/lib.rs \
    && cargo build --release --locked --no-default-features \
    && rm -rf src
COPY schemas schemas
COPY src src
RUN touch src/main.rs src/lib.rs && cargo build --release --locked --no-default-features

# glibc, libgcc and libssl3, no shell or package manager; runs as uid 65532.
FROM gcr.io/distroless/cc-debian12:nonroot
COPY --from=build /build/target/release/agentcon-supplier /usr/local/bin/agentcon-supplier
ENV SUPPLIER_PORT=6000
# The HTTP interface listens on loopback only; probe it with the binary itself.
HEALTHCHECK --interval=15s --timeout=5s CMD ["/usr/local/bin/agentcon-supplier", "ready-probe"]
ENTRYPOINT ["/usr/local/bin/agentcon-supplier"]
