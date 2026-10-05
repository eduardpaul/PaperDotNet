import type { ApprovalReviewData } from '@paperdotnet/client';
import type { ComponentType } from 'react';
import { PdfComposition } from '@/features/approvals/pdf-composition';
import { ImageComparison } from '@/features/approvals/image-comparison';

export interface ApprovalReviewProps {
  approvalId: string;
  review: ApprovalReviewData;
  onReady: (ready: boolean) => void;
}

export interface ApprovalReviewRenderer {
  component: ComponentType<ApprovalReviewProps>;
  approvedLabel: string;
  rejectedLabel: string;
}

/** Build-time review renderers, selected by the tenant-gated server provider's descriptor. */
export const approvalReviewRenderers: Record<string, ApprovalReviewRenderer> = {
  pdfComposition: {
    component: PdfComposition,
    approvedLabel: 'Approve PDF',
    rejectedLabel: 'Keep originals',
  },
  imageComparison: {
    component: ImageComparison,
    approvedLabel: 'Store optimized file',
    rejectedLabel: 'Keep original',
  },
};
