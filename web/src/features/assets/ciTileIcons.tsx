import {
  Archive, Boxes, Building2, CircleAlert, Clock, Cpu, HardDrive, Laptop, MapPin, Monitor,
  Network, Pin, Printer, Server, ShieldAlert, ShieldCheck, Smartphone, Tag, Users, Wrench,
} from 'lucide-react'

export type TileIconKey = string

/**
 * The icons a pinned tile may carry.
 *
 * A curated set rather than the whole of lucide: a grid of a thousand icons is not a choice anybody
 * can make, and every icon named here is a real import that lands in the bundle. These cover what
 * the estate is actually sliced by — kinds of hardware, where it is, who holds it, and what is wrong
 * with it.
 */
export const tileIcons: readonly { key: TileIconKey; label: string; icon: typeof Pin }[] = [
  { key: 'pin', label: 'Pin', icon: Pin },
  { key: 'boxes', label: 'Boxes', icon: Boxes },
  { key: 'laptop', label: 'Laptop', icon: Laptop },
  { key: 'monitor', label: 'Desktop', icon: Monitor },
  { key: 'printer', label: 'Printer', icon: Printer },
  { key: 'smartphone', label: 'Mobile', icon: Smartphone },
  { key: 'server', label: 'Server', icon: Server },
  { key: 'network', label: 'Network', icon: Network },
  { key: 'harddrive', label: 'Storage', icon: HardDrive },
  { key: 'cpu', label: 'Hardware', icon: Cpu },
  { key: 'building', label: 'Site', icon: Building2 },
  { key: 'mappin', label: 'Location', icon: MapPin },
  { key: 'users', label: 'People', icon: Users },
  { key: 'tag', label: 'Tag', icon: Tag },
  { key: 'clock', label: 'Ageing', icon: Clock },
  { key: 'wrench', label: 'Repair', icon: Wrench },
  { key: 'shieldalert', label: 'Warranty risk', icon: ShieldAlert },
  { key: 'shieldcheck', label: 'Covered', icon: ShieldCheck },
  { key: 'alert', label: 'Attention', icon: CircleAlert },
  { key: 'archive', label: 'Archive', icon: Archive },
]

export const defaultTileIcon: TileIconKey = 'pin'

/**
 * The icon for a key, falling back rather than throwing.
 *
 * A stored key may name an icon this version no longer offers — the tiles live in the reader's own
 * browser and outlive any one release — and a tile that cannot be drawn is worse than one drawn with
 * a pin.
 */
export function tileIcon(key: TileIconKey | undefined): typeof Pin {
  return tileIcons.find((option) => option.key === key)?.icon ?? Pin
}
