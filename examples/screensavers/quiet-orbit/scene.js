const canvas = document.querySelector("canvas");
const context = canvas.getContext("2d", { alpha: false });
function resize() {
  canvas.width = Math.min(innerWidth, 1920);
  canvas.height = Math.min(innerHeight, 1080);
}
addEventListener("resize", resize);
resize();
function draw(time) {
  const { width, height } = canvas;
  const radius = Math.min(width, height) * 0.27;
  const angle = time / 25000;
  context.fillStyle = "#070e1b";
  context.fillRect(0, 0, width, height);
  context.strokeStyle = "#20354d";
  context.lineWidth = 2;
  context.beginPath();
  context.arc(width / 2, height / 2, radius, 0, Math.PI * 2);
  context.stroke();
  const x = width / 2 + Math.cos(angle) * radius;
  const y = height / 2 + Math.sin(angle) * radius;
  const glow = context.createRadialGradient(x, y, 2, x, y, 38);
  glow.addColorStop(0, "#d8fbff");
  glow.addColorStop(0.2, "#58d8ed");
  glow.addColorStop(1, "#070e1b");
  context.fillStyle = glow;
  context.fillRect(x - 38, y - 38, 76, 76);
  requestAnimationFrame(draw);
}
requestAnimationFrame(draw);
