variable "aws_region" {
  description = "AWS region for the demo EC2 (pick one close to you; Free Tier availability varies)."
  type        = string
  default     = "us-east-1"
}

variable "project_name" {
  description = "Name prefix for AWS resources."
  type        = string
  default     = "transaction-import"
}

variable "instance_type" {
  description = "EC2 size. t3.micro is Free Tier eligible on many accounts."
  type        = string
  default     = "t3.micro"
}

variable "ssh_public_key_path" {
  description = "Path to the SSH public key uploaded to AWS (e.g. ~/.ssh/id_ed25519.pub)."
  type        = string
}

variable "ssh_private_key_path" {
  description = "Path to the matching private key (used only for optional remote bootstrap)."
  type        = string
  default     = ""
}

variable "allowed_ssh_cidr" {
  description = "CIDR allowed to SSH (your public IP /32 is safest)."
  type        = string
  default     = "0.0.0.0/0"
}

variable "duckdns_subdomain" {
  description = "DuckDNS subdomain only (e.g. abzlab-import → abzlab-import.duckdns.org). Leave empty to skip auto-update."
  type        = string
  default     = ""
}

variable "duckdns_token" {
  description = "DuckDNS account token (sensitive). Leave empty to skip auto-update."
  type        = string
  default     = ""
  sensitive   = true
}

variable "domain_name" {
  description = "Public hostname for Nginx / TLS (usually <subdomain>.duckdns.org)."
  type        = string
  default     = ""
}

variable "enable_https" {
  description = "If true and domain_name is set, run certbot on the instance after create (needs SSH private key)."
  type        = bool
  default     = false
}

variable "certbot_email" {
  description = "Email for Let's Encrypt registration (required when enable_https=true)."
  type        = string
  default     = ""
}

variable "app_dir" {
  description = "Directory on the server for compose + .env (matches CD DEPLOY_COMPOSE_DIR)."
  type        = string
  default     = "/usr/src/apps/transaction-import"
}
