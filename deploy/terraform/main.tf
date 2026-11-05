data "aws_ami" "ubuntu" {
  most_recent = true
  owners      = ["099720109477"] # Canonical

  filter {
    name   = "name"
    values = ["ubuntu/images/hvm-ssd-gp3/ubuntu-noble-24.04-amd64-server-*"]
  }

  filter {
    name   = "virtualization-type"
    values = ["hvm"]
  }
}

data "aws_vpc" "default" {
  default = true
}

data "aws_subnets" "default" {
  filter {
    name   = "vpc-id"
    values = [data.aws_vpc.default.id]
  }
}

resource "aws_key_pair" "deploy" {
  key_name   = "${var.project_name}-key"
  public_key = file(pathexpand(var.ssh_public_key_path))
}

resource "aws_security_group" "app" {
  name        = "${var.project_name}-sg"
  description = "SSH + HTTP/HTTPS for transaction-import demo"
  vpc_id      = data.aws_vpc.default.id

  ingress {
    description = "SSH"
    from_port   = 22
    to_port     = 22
    protocol    = "tcp"
    cidr_blocks = [var.allowed_ssh_cidr]
  }

  ingress {
    description = "HTTP"
    from_port   = 80
    to_port     = 80
    protocol    = "tcp"
    cidr_blocks = ["0.0.0.0/0"]
  }

  ingress {
    description = "HTTPS"
    from_port   = 443
    to_port     = 443
    protocol    = "tcp"
    cidr_blocks = ["0.0.0.0/0"]
  }

  egress {
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }

  tags = {
    Name    = "${var.project_name}-sg"
    Project = var.project_name
  }
}

resource "aws_instance" "app" {
  ami                         = data.aws_ami.ubuntu.id
  instance_type               = var.instance_type
  key_name                    = aws_key_pair.deploy.key_name
  subnet_id                   = data.aws_subnets.default.ids[0]
  vpc_security_group_ids      = [aws_security_group.app.id]
  associate_public_ip_address = true

  root_block_device {
    volume_size = 20
    volume_type = "gp3"
  }

  user_data = templatefile("${path.module}/templates/cloud-init.yaml.tftpl", {
    app_dir     = var.app_dir
    domain_name = var.domain_name
  })

  tags = {
    Name    = "${var.project_name}-ec2"
    Project = var.project_name
  }
}

resource "aws_eip" "app" {
  domain   = "vpc"
  instance = aws_instance.app.id

  tags = {
    Name    = "${var.project_name}-eip"
    Project = var.project_name
  }
}

# Point DuckDNS at the Elastic IP (optional).
resource "null_resource" "duckdns" {
  count = var.duckdns_subdomain != "" && var.duckdns_token != "" ? 1 : 0

  triggers = {
    ip        = aws_eip.app.public_ip
    subdomain = var.duckdns_subdomain
  }

  provisioner "local-exec" {
    command = <<-EOT
      set -euo pipefail
      resp=$(curl -fsS "https://www.duckdns.org/update?domains=${var.duckdns_subdomain}&token=${var.duckdns_token}&ip=${aws_eip.app.public_ip}")
      echo "DuckDNS response: $resp"
      test "$resp" = "OK"
    EOT
  }
}

# Optional: wait for SSH, then issue Let's Encrypt cert on the box.
resource "null_resource" "https_cert" {
  count = var.enable_https && var.domain_name != "" && var.ssh_private_key_path != "" ? 1 : 0

  depends_on = [
    aws_eip.app,
    null_resource.duckdns,
  ]

  triggers = {
    instance_id = aws_instance.app.id
    domain      = var.domain_name
  }

  connection {
    type        = "ssh"
    user        = "ubuntu"
    host        = aws_eip.app.public_ip
    private_key = file(pathexpand(var.ssh_private_key_path))
    timeout     = "10m"
  }

  provisioner "remote-exec" {
    inline = [
      "cloud-init status --wait || true",
      "sudo sed -i 's/server_name _;/server_name ${var.domain_name};/' /etc/nginx/sites-available/transaction-import",
      "sudo nginx -t && sudo systemctl reload nginx",
      "for i in $(seq 1 30); do getent hosts ${var.domain_name} >/dev/null && break; sleep 5; done",
      "sudo certbot --nginx -d ${var.domain_name} --non-interactive --agree-tos -m ${var.certbot_email} --redirect",
    ]
  }
}
