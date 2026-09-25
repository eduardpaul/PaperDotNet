from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .template_change import TemplateChange

@dataclass
class TemplateResult(AdditionalDataHolder, Parsable):
    """
    Outcome of an apply: the changes made (or planned, in a dry run) and warnings.
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The changes property
    changes: Optional[list[TemplateChange]] = None
    # The dryRun property
    dry_run: Optional[bool] = None
    # The warnings property
    warnings: Optional[list[str]] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> TemplateResult:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: TemplateResult
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return TemplateResult()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .template_change import TemplateChange

        from .template_change import TemplateChange

        fields: dict[str, Callable[[Any], None]] = {
            "changes": lambda n : setattr(self, 'changes', n.get_collection_of_object_values(TemplateChange)),
            "dryRun": lambda n : setattr(self, 'dry_run', n.get_bool_value()),
            "warnings": lambda n : setattr(self, 'warnings', n.get_collection_of_primitive_values(str)),
        }
        return fields
    
    def serialize(self,writer: SerializationWriter) -> None:
        """
        Serializes information the current object
        param writer: Serialization writer to use to serialize this model
        Returns: None
        """
        if writer is None:
            raise TypeError("writer cannot be null.")
        writer.write_collection_of_object_values("changes", self.changes)
        writer.write_bool_value("dryRun", self.dry_run)
        writer.write_collection_of_primitive_values("warnings", self.warnings)
        writer.write_additional_data_value(self.additional_data)
    

