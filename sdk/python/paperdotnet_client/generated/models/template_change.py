from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .template_change_action import TemplateChangeAction

@dataclass
class TemplateChange(AdditionalDataHolder, Parsable):
    """
    A change an apply makes (or would make, in a dry run).
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The action property
    action: Optional[TemplateChangeAction] = None
    # The detail property
    detail: Optional[str] = None
    # The kind property
    kind: Optional[str] = None
    # The name property
    name: Optional[str] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> TemplateChange:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: TemplateChange
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return TemplateChange()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .template_change_action import TemplateChangeAction

        from .template_change_action import TemplateChangeAction

        fields: dict[str, Callable[[Any], None]] = {
            "action": lambda n : setattr(self, 'action', n.get_enum_value(TemplateChangeAction)),
            "detail": lambda n : setattr(self, 'detail', n.get_str_value()),
            "kind": lambda n : setattr(self, 'kind', n.get_str_value()),
            "name": lambda n : setattr(self, 'name', n.get_str_value()),
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
        writer.write_enum_value("action", self.action)
        writer.write_str_value("detail", self.detail)
        writer.write_str_value("kind", self.kind)
        writer.write_str_value("name", self.name)
        writer.write_additional_data_value(self.additional_data)
    

