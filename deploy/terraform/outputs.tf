output "public_ip" {
  description = "Elastic IP of the demo server."
  value       = aws_eip.app.public_ip
}

output "ssh_command" {
  description = "SSH into the instance."
  value       = "ssh -i ${var.ssh_private_key_path != "" ? var.ssh_private_key_path : "<your-private-key>"} ubuntu@${aws_eip.app.public_ip}"
}

output "app_dir" {
  description = "Compose directory on the server (set DEPLOY_COMPOSE_DIR to this)."
  value       = var.app_dir
}

output "public_base_url" {
  description = "URL to put in README / reviewer guide once DNS + TLS (or HTTP) are ready."
  value       = var.domain_name != "" ? "https://${var.domain_name}" : "http://${aws_eip.app.public_ip}"
}

output "swagger_url" {
  description = "Swagger entry point for evaluators."
  value       = var.domain_name != "" ? "https://${var.domain_name}/swagger" : "http://${aws_eip.app.public_ip}/swagger"
}

output "github_cd_secrets" {
  description = "Values to paste into the GitHub Environment production secrets."
  value = {
    DEPLOY_HOST        = aws_eip.app.public_ip
    DEPLOY_USER        = "ubuntu"
    DEPLOY_PORT        = "22"
    DEPLOY_COMPOSE_DIR = var.app_dir
    DEPLOY_SSH_KEY     = "(private key contents matching the uploaded public key)"
  }
}
