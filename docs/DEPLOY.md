# Deploy (AWS Free Tier + DuckDNS + Nginx)

This guide stands up a public demo so reviewers can open Swagger. Infra lives in
[`deploy/terraform`](../deploy/terraform). Local testing still uses `docker-compose.yml`;
the server uses **`docker-compose.prod.yml`** only.

**Target layout on the VM:** `/usr/src/apps/transaction-import`

---

## 0. What you get

| Piece | Role |
| --- | --- |
| EC2 (`t3.micro`) + Elastic IP | Always-on public IP (Free Tier eligible on new accounts — verify yours) |
| Docker + Compose | Runs postgres, redis, migrator, api, worker from GHCR |
| Nginx | Reverse proxy `:80`/`:443` → API `127.0.0.1:8080` |
| DuckDNS | Free hostname `you.duckdns.org` |
| Certbot | Free HTTPS (Let's Encrypt) |

Rough cost if you stay on Free Tier / Free plan: **$0** for the demo window. Watch EBS size,
leaving the EIP unused, and converting off the Free plan.

---

## 1. One-time accounts

1. Create / use an **AWS** account with Free Tier: [aws.amazon.com/free](https://aws.amazon.com/free/)
2. Install locally: [AWS CLI](https://docs.aws.amazon.com/cli/), [Terraform](https://developer.hashicorp.com/terraform/install), Docker (for local only).
3. Configure AWS credentials (`aws configure` or env vars).
4. Create a free hostname at [duckdns.org](https://www.duckdns.org/) (GitHub login works).
   - Create subdomain e.g. `abzlab-import` → `abzlab-import.duckdns.org`
   - Copy the **token**

---

## 2. Terraform (EC2 + Docker + Nginx)

```bash
cd deploy/terraform
cp terraform.tfvars.example terraform.tfvars
# edit terraform.tfvars: SSH key paths, DuckDNS subdomain/token, domain_name, certbot_email
```

Recommended `terraform.tfvars` highlights:

```hcl
ssh_public_key_path  = "/Users/YOU/.ssh/id_ed25519.pub"
ssh_private_key_path = "/Users/YOU/.ssh/id_ed25519"
allowed_ssh_cidr     = "YOUR.PUBLIC.IP/32"   # safer than 0.0.0.0/0

duckdns_subdomain = "abzlab-import"
duckdns_token     = "…"
domain_name       = "abzlab-import.duckdns.org"
enable_https      = true
certbot_email     = "you@example.com"
```

```bash
terraform init
terraform apply
terraform output
```

Save:

- `public_ip`
- `public_base_url` / `swagger_url`
- `github_cd_secrets` (for GitHub Environment **production**)

Cloud-init installs Docker, Nginx, and creates `/usr/src/apps/transaction-import`. First boot can take **3–5 minutes**.

```bash
# wait until ssh works
ssh -i ~/.ssh/id_ed25519 ubuntu@$(terraform output -raw public_ip)
```

---

## 3. Put Compose on the server

From the repo root (after `terraform apply`):

```bash
chmod +x deploy/scripts/*.sh
./deploy/scripts/sync-compose-to-server.sh \
  ubuntu@$(cd deploy/terraform && terraform output -raw public_ip) \
  ~/.ssh/id_ed25519
```

On the server, edit `.env` (DB password, optional Cloudinary). Then either wait for **CD** or start manually:

```bash
ssh -i ~/.ssh/id_ed25519 ubuntu@YOUR_IP
cd /usr/src/apps/transaction-import
# if GHCR packages are private:
echo "$GHCR_TOKEN" | docker login ghcr.io -u YOUR_GITHUB_USER --password-stdin
docker compose -f docker-compose.prod.yml pull
docker compose -f docker-compose.prod.yml up -d
curl -fsS http://127.0.0.1:8080/health/ready
```

Nginx already proxies public `80`/`443` → `8080`.

---

## 4. Wire GitHub CD secrets

GitHub repo → **Settings → Environments → production** (create if missing):

| Secret | Value |
| --- | --- |
| `DEPLOY_HOST` | Elastic IP from `terraform output` |
| `DEPLOY_USER` | `ubuntu` |
| `DEPLOY_PORT` | `22` |
| `DEPLOY_COMPOSE_DIR` | `/usr/src/apps/transaction-import` |
| `DEPLOY_SSH_KEY` | **private** key PEM contents (same key as Terraform) |

Ensure the matching public key is what Terraform uploaded (it is). Push to `main` to build GHCR images and deploy.

Make packages pullable: for private GHCR images the CD job already `docker login`s with `GITHUB_TOKEN`. Manual pulls need a PAT with `read:packages`.

---

## 5. Put the public URL in the docs

After HTTPS works:

```bash
./deploy/scripts/update-readme-url.sh https://abzlab-import.duckdns.org
# commit + push so reviewers see the real link
```

That replaces `https://YOUR_PUBLIC_HOST` in README, SUBMISSION, and REVIEWER_TEST_GUIDE.

Smoke:

- `https://YOUR_HOST/swagger`
- `https://YOUR_HOST/health/ready`
- follow [`REVIEWER_TEST_GUIDE.md`](REVIEWER_TEST_GUIDE.md)

---

## 6. Tear down (stop AWS charges)

```bash
cd deploy/terraform
terraform destroy
```

Also delete unused Elastic IPs / volumes in the AWS console if anything is left.

---

## Troubleshooting

| Symptom | Check |
| --- | --- |
| SSH timeout | Security group + `allowed_ssh_cidr`; instance running |
| Nginx 502 | `docker compose -f docker-compose.prod.yml ps` — API healthy on `:8080`? |
| Certbot fail | DuckDNS A record = EIP; wait for DNS; port 80 open |
| GHCR pull denied | `docker login ghcr.io`; package visibility / PAT |
| CD SSH fail | `DEPLOY_SSH_KEY` is private key; user `ubuntu`; compose dir exists |

More: root [README](../README.md) · [`docker-compose.prod.yml`](../docker-compose.prod.yml) · [SUBMISSION](SUBMISSION.md)
