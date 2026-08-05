export type LiquidGlassMaterial = {
  reverseDisplacement: number;
  edgeCurvature: number;
  opticalThickness: number;
  edgeBandRatio: number;
  refractionVisibleRatio: number;
  blendFeatherPx: number;
  frostedStrength: number;
  frostedAttenuation: number;
  blurSpacingPx: number;
  dispersionStrength: number;
  highlightStrength: number;
  tintColor: [number, number, number];
  tintMix: number;
};

export type LiquidGlassStats = {
  supported: boolean;
  renderer: "WebGL2 analytic path tracing";
  accumulatedFrames: number;
  pathsPerPixel: number;
  capsuleAccumulatedFrames: number;
  capsulePathsPerPixel: number;
  capsuleMaxAccumulation: number;
  frameMs: number;
  pixelRatio: number;
  performanceProfile: "desktop" | "mobile";
  blurSampleCount: number;
  targetPrecision: "rgba8" | "rgba16f";
};

export type LiquidGlassMobilePerformance = {
  maxDpr: number;
  pathsPerPixel: number;
  maxAccumulation: number;
  blurSampleCount: number;
  captureMinIntervalMs: number;
  preferFloatTargets: boolean;
  capsuleOnTouch: boolean;
  capsulePathsPerPixel: number;
  capsuleMaxAccumulation: number;
};

export type LiquidGlassReferenceParameters = {
  material: Readonly<LiquidGlassMaterial>;
  optics: Readonly<{
    refractiveIndices: Readonly<{ red: number; green: number; blue: number }>;
    roughness: number;
    cameraRayScale: number;
    minimumRayZ: number;
    insideTravelBasePx: number;
    insideTravelInteriorScale: number;
    exitTravelBasePx: number;
    reverseMaxPixels: number;
    lensMix: number;
    textMaskProtection: number;
    frostTintColor: readonly [number, number, number];
    highlightAWidth: number;
    highlightACenter: number;
    highlightBWidth: number;
    highlightBCenter: number;
  }>;
  rendering: Readonly<{
    pathsPerPixel: number;
    maxAccumulation: number;
    maxDpr: number;
    blurSampleCount: number;
    captureMinIntervalMs: number;
    preferFloatTargets: boolean;
    capsulePathsPerPixel: number;
    capsuleMaxAccumulation: number;
  }>;
  capsule: Readonly<{ widthRatio: number; heightScale: number }>;
  navigation: Readonly<{
    widthVw: number;
    heightPx: number;
    bottomPx: number;
    mobileWidthVw: number;
    mobileHeightPx: number;
    mobileBottomPx: number;
    canvasHeightScale: number;
  }>;
};

export type LiquidGlassOptions = {
  navElement: HTMLElement;
  pointerElement?: HTMLElement;
  canvas: HTMLCanvasElement;
  sceneRoot?: HTMLElement;
  maxDpr?: number;
  pathsPerPixel?: number;
  maxAccumulation?: number;
  capsuleWidthRatio?: number;
  capsuleHeightScale?: number;
  captureBackground?: string;
  observe?: boolean;
  allowCrossOriginImages?: boolean;
  captureNavigationContent?: boolean;
  autoLabelContrast?: boolean;
  labelSelector?: string;
  blurSampleCount?: number;
  captureMinIntervalMs?: number;
  preferFloatTargets?: boolean;
  capsuleOnTouch?: boolean;
  capsulePathsPerPixel?: number;
  capsuleMaxAccumulation?: number;
  mobilePerformance?: boolean | Partial<LiquidGlassMobilePerformance>;
  material?: Partial<LiquidGlassMaterial>;
  onReady?: () => void;
  onError?: (error: Error) => void;
  onStats?: (stats: LiquidGlassStats) => void;
};

export type LiquidGlassElementTarget<T extends Element = HTMLElement> = T | string;

export type MountLiquidGlassOptions = Omit<
  LiquidGlassOptions,
  "navElement" | "canvas" | "sceneRoot"
> & {
  nav?: LiquidGlassElementTarget<HTMLElement>;
  navElement?: LiquidGlassElementTarget<HTMLElement>;
  canvas?: LiquidGlassElementTarget<HTMLCanvasElement>;
  scene?: LiquidGlassElementTarget<HTMLElement>;
  sceneRoot?: LiquidGlassElementTarget<HTMLElement>;
  autoStart?: boolean;
};

export type CapsuleRectInput = {
  navLeft: number;
  navTop: number;
  navWidth: number;
  navHeight: number;
  pointerX: number;
  widthRatio?: number;
  heightScale?: number;
};

export type Rectangle = {
  x: number;
  y: number;
  width: number;
  height: number;
};

export declare const DEFAULT_MATERIAL: Readonly<LiquidGlassMaterial>;
export declare const LIQUID_GLASS_REFERENCE: Readonly<LiquidGlassReferenceParameters>;

export declare const DEFAULT_OPTIONS: Readonly<{
  maxDpr: number;
  pathsPerPixel: number;
  maxAccumulation: number;
  capsuleWidthRatio: number;
  capsuleHeightScale: number;
  captureBackground: string;
  observe: boolean;
  allowCrossOriginImages: boolean;
  captureNavigationContent: boolean;
  autoLabelContrast: boolean;
  labelSelector: string;
  mobilePerformance: boolean;
}>;

export declare const DEFAULT_MOBILE_PERFORMANCE: Readonly<LiquidGlassMobilePerformance>;

export declare function detectMobileDevice(navigatorLike?: {
  userAgent?: string;
  platform?: string;
  maxTouchPoints?: number;
  userAgentData?: { mobile?: boolean };
}): boolean;

export declare function resolvePerformanceOptions(
  options?: Partial<LiquidGlassOptions>,
  navigatorLike?: {
    userAgent?: string;
    platform?: string;
    maxTouchPoints?: number;
    userAgentData?: { mobile?: boolean };
  },
): LiquidGlassMobilePerformance & { mobileDetected: boolean; mobileProfile: boolean };

export declare function computeCapsuleRect(input: CapsuleRectInput): Rectangle;
export declare function srgbChannelToLinear(channel: number): number;
export declare function relativeLuminance(red: number, green: number, blue: number): number;
export declare function chooseReadableTextTone(luminance: number): "light" | "dark";

export declare class LiquidGlassNavigation {
  constructor(options: LiquidGlassOptions);
  isSupported(): boolean;
  start(): boolean;
  refreshScene(): void;
  resize(): void;
  setPointer(x: number, y?: number): void;
  hideCapsule(): void;
  setMaterial(patch: Partial<LiquidGlassMaterial>): void;
  setCapsuleGeometry(options: { widthRatio?: number; heightScale?: number }): void;
  setMaxDpr(value: number): void;
  setMaxAccumulation(value: number): void;
  setPathsPerPixel(value: number): boolean;
  getStats(): LiquidGlassStats;
  dispose(): void;
}

export declare function mountLiquidGlassNavigation(
  options: MountLiquidGlassOptions,
): LiquidGlassNavigation;
export declare function mountLiquidGlassNavigation(
  nav: LiquidGlassElementTarget<HTMLElement>,
  options?: Omit<MountLiquidGlassOptions, "nav" | "navElement">,
): LiquidGlassNavigation;
export declare const liquidGlass: typeof mountLiquidGlassNavigation;
