import { Boxes } from "lucide-react";

export function Brand({ compact = false }: { compact?: boolean }) {
  return (
    <div className={`brand ${compact ? "brand--compact" : ""}`}>
      <span className="brand__mark"><Boxes aria-hidden="true" /></span>
      {!compact && <span className="brand__copy"><strong>Lumina</strong><small>CI control plane</small></span>}
    </div>
  );
}
