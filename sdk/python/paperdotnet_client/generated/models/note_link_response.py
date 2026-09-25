from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .linked_note import LinkedNote

@dataclass
class NoteLinkResponse(AdditionalDataHolder, Parsable):
    """
    A wiki link of a note; LinkedNote? NoteLinkResponse.Note is the note it points to (null when none exists or the caller cannot read it).
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The alias property
    alias: Optional[str] = None
    # The embed property
    embed: Optional[bool] = None
    # The heading property
    heading: Optional[str] = None
    # The note property
    note: Optional[LinkedNote] = None
    # The target property
    target: Optional[str] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> NoteLinkResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: NoteLinkResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return NoteLinkResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .linked_note import LinkedNote

        from .linked_note import LinkedNote

        fields: dict[str, Callable[[Any], None]] = {
            "alias": lambda n : setattr(self, 'alias', n.get_str_value()),
            "embed": lambda n : setattr(self, 'embed', n.get_bool_value()),
            "heading": lambda n : setattr(self, 'heading', n.get_str_value()),
            "note": lambda n : setattr(self, 'note', n.get_object_value(LinkedNote)),
            "target": lambda n : setattr(self, 'target', n.get_str_value()),
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
        writer.write_str_value("alias", self.alias)
        writer.write_bool_value("embed", self.embed)
        writer.write_str_value("heading", self.heading)
        writer.write_object_value("note", self.note)
        writer.write_str_value("target", self.target)
        writer.write_additional_data_value(self.additional_data)
    

